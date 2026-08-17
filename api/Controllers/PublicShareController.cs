using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Security;
using PatientDataPortal.Api.Sharing;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
public sealed class PublicShareController : ControllerBase
{
    [HttpGet("api/public/share/{token}")]
    public async Task<IActionResult> View(string token, [FromServices] IPublicShareService shares, [FromServices] IPublicShareFailureLimiter failures, CancellationToken cancellationToken)
    {
        var share = await shares.FindActiveAsync(token, cancellationToken);
        if (share is null) return Unavailable(failures);
        SetPrivacyHeaders();
        return Ok(new PublicShareView(share.ResourceType));
    }

    [HttpGet("api/public/share/{token}/content")]
    public async Task<IActionResult> Content(string token, [FromQuery] string? disposition, [FromServices] IPublicShareService shares, [FromServices] IPublicShareStorage storage, [FromServices] IPublicShareFailureLimiter failures, [FromServices] IAuditWriter audit, CancellationToken cancellationToken)
    {
        var share = await shares.FindActiveAsync(token, cancellationToken);
        if (share is null) return Unavailable(failures);

        await using var content = await storage.OpenReadAsync(share, cancellationToken);
        if (content is null) return NotFound();
        await audit.WriteAllowedAsync(new AuditEvent(null, "anonymous", "shared_content_delivered", "share_link", share.Id.ToString(), "allowed"), cancellationToken);

        SetPrivacyHeaders();
        Response.ContentType = content.ContentType;
        var mode = string.Equals(disposition, "inline", StringComparison.OrdinalIgnoreCase) ? "inline" : "attachment";
        Response.Headers["Content-Disposition"] = $"{mode}; filename=\"{content.FileName}\"";
        await content.Stream.CopyToAsync(Response.Body, cancellationToken);
        return new EmptyResult();
    }

    private IActionResult Unavailable(IPublicShareFailureLimiter failures)
    {
        SetPrivacyHeaders();
        return failures.RecordFailure(RequestKey()) ? NotFound() : StatusCode(StatusCodes.Status429TooManyRequests);
    }

    private string RequestKey() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private void SetPrivacyHeaders()
    {
        Response.Headers["Cache-Control"] = "private, no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}

public sealed record PublicShareView(string ResourceType);
