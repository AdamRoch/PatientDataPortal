using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Security;
using PatientDataPortal.Api.Studies;
using PatientDataPortal.Api.Imaging;
using PatientDataPortal.Api.Reports;
using PatientDataPortal.Api.Cine;
using PatientDataPortal.Api.Sharing;
using System.Security.Claims;
using NodaTime;
using System.Net.Mail;

namespace PatientDataPortal.Api.Controllers;

// These route roots are protected now so later resource slices cannot accidentally expose an
// unverified patient while they add their resource-specific handlers beneath the same roots.
[ApiController]
[RequireRole(AppRole.Patient)]
[RequireVerifiedPatient]
public sealed class VerifiedPatientResourcesController : ControllerBase
{
    [HttpPost("api/share")]
    public async Task<ActionResult<MintedShare>> Share(
        ShareCreateRequest request,
        [FromServices] IShareService shares,
        CancellationToken cancellationToken)
    {
        if (request.ResourceType is not ("image" or "report") || !IsEmailAddress(request.RecipientEmail))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["share"] = ["resourceType must be image or report and recipientEmail must be a valid email address."] }));
        var minted = await shares.MintAsync(UserId(), new ShareRequest(request.ResourceType, request.ResourceId, request.RecipientEmail), cancellationToken);
        return minted is null ? NotFound() : Ok(minted);
    }

    [HttpGet("api/shares")]
    public async Task<ActionResult<IReadOnlyList<ManagedShare>>> Shares(
        [FromServices] IShareManagementService shares,
        CancellationToken cancellationToken) => Ok(await shares.ListAsync(UserId(), cancellationToken));

    [HttpDelete("api/shares/{shareId:guid}")]
    public async Task<IActionResult> RevokeShare(
        Guid shareId,
        [FromServices] IShareManagementService shares,
        CancellationToken cancellationToken) => await shares.RevokeAsync(UserId(), shareId, cancellationToken) ? NoContent() : NotFound();

    [HttpGet("api/studies")]
    public async Task<ActionResult<IReadOnlyList<StudyListItem>>> Studies(
        [FromServices] IStudyRepository studies,
        CancellationToken cancellationToken) => Ok(await studies.ListCompletedForPatientAsync(UserId(), cancellationToken));

    [HttpGet("api/images/{id:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ImageAccess>> Image(Guid id, [FromServices] IImageAccessService images, CancellationToken cancellationToken)
    {
        var image = await images.MintForPatientAsync(id, UserId(), cancellationToken);
        return image is null ? new ActionResult<ImageAccess>(NotFoundWithoutBody()) : Ok(image);
    }

    [HttpGet("api/reports")]
    public async Task<ActionResult<IReadOnlyList<SignedReportListItem>>> Reports(
        [FromServices] IReportRepository reports,
        CancellationToken cancellationToken) => Ok(await reports.ListSignedForPatientAsync(UserId(), cancellationToken));

    [HttpGet("api/reports/{reportId:guid}/view")]
    public async Task<IActionResult> ViewReport(
        Guid reportId,
        [FromServices] IReportRepository reports,
        [FromServices] IReportStorage storage,
        [FromServices] IAuditWriter auditWriter,
        CancellationToken cancellationToken)
    {
        var report = await reports.FindSignedForPatientAsync(reportId, UserId(), cancellationToken);
        if (report is null) return NotFound();

        var url = await storage.CreateSignedReadUrlAsync(report.StoragePath, cancellationToken);
        await auditWriter.WriteAllowedAsync(new AuditEvent(UserId().ToString(), "patient", "report_view", "report", report.Id.ToString(), "allowed"), cancellationToken);
        return Ok(new { url = url.ToString() });
    }

    [HttpGet("api/images")] public IActionResult Images() => NotFound();
    [HttpGet("api/cine")] public IActionResult Cine() => NotFound();

    [HttpGet("api/cine/{id:guid}")]
    public async Task<ActionResult<CineManifestResponse>> CineManifest(
        Guid id,
        [FromServices] ICineRepository cine,
        [FromServices] IAuditWriter audit,
        CancellationToken cancellationToken)
    {
        var clip = await cine.GetOwnedAsync(id, UserId(), cancellationToken);
        if (clip is null)
        {
            await WriteDeniedAsync(audit, id, cancellationToken);
            return new ActionResult<CineManifestResponse>(NotFoundWithoutBody());
        }

        await WriteGrantedAsync(audit, id, cancellationToken);
        return Ok(new CineManifestResponse(clip.Id, clip.Manifest));
    }

    [HttpPost("api/cine/{id:guid}/frame-urls")]
    public async Task<ActionResult<CineFrameUrlBatchResponse>> FrameUrls(
        Guid id,
        CineFrameUrlBatchRequest request,
        [FromServices] ICineRepository cine,
        [FromServices] ICineFrameUrlSigner signer,
        [FromServices] IAuditWriter audit,
        [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (request.StartFrame < 0 || request.Count is < 1 or > CineFrameUrlBatchRequest.MaximumCount)
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["batch"] = ["startFrame must be non-negative and count must be between 1 and 50."] }));

        var clip = await cine.GetOwnedAsync(id, UserId(), cancellationToken);
        if (clip is null)
        {
            await WriteDeniedAsync(audit, id, cancellationToken);
            return new ActionResult<CineFrameUrlBatchResponse>(NotFoundWithoutBody());
        }

        var paths = clip.FramePaths.Skip(request.StartFrame).Take(request.Count).ToArray();
        await WriteGrantedAsync(audit, id, cancellationToken);
        var urls = await signer.MintAsync(paths, request.StartFrame, cancellationToken);
        if (urls.Count > paths.Length) throw new InvalidOperationException("Storage returned too many cine frame URLs.");
        return Ok(new CineFrameUrlBatchResponse(urls, clock.GetCurrentInstant().ToDateTimeOffset().AddSeconds(CineFrameUrlSigner.ExpirySeconds)));
    }

    private Task WriteGrantedAsync(IAuditWriter audit, Guid clipId, CancellationToken cancellationToken) => audit.WriteAsync(new AuditEvent(UserId().ToString(), "patient", "content_access_granted", "cine_clip", clipId.ToString(), "allowed"), cancellationToken);
    private Task WriteDeniedAsync(IAuditWriter audit, Guid clipId, CancellationToken cancellationToken) => audit.WriteDeniedAsync(new AuditEvent(UserId().ToString(), "patient", "content_access_denied", "cine_clip", clipId.ToString(), "denied"), cancellationToken);
    private EmptyResult NotFoundWithoutBody()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return new EmptyResult();
    }
    private static bool IsEmailAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return new MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }
    private Guid UserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}

public sealed record CineManifestResponse(Guid Id, System.Text.Json.JsonElement Manifest);
public sealed record CineFrameUrlBatchRequest(int StartFrame, int Count)
{
    public const int MaximumCount = 50;
}
public sealed record CineFrameUrlBatchResponse(IReadOnlyList<SignedFrameUrl> Frames, DateTimeOffset ExpiresAt);
public sealed record ShareCreateRequest(string ResourceType, Guid ResourceId, string RecipientEmail);
