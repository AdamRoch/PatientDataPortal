using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/identity")]
[RequireRole(AppRole.Patient)]
public sealed class IdentityVerificationController(IIdentityVerificationService identityVerification) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var accountId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return Ok(new { verified = await identityVerification.IsVerifiedPatientAsync(accountId, cancellationToken) });
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] IdentityVerificationRequest request, CancellationToken cancellationToken)
    {
        var accountId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var emailVerified = string.Equals(User.FindFirstValue("email_verified"), "true", StringComparison.Ordinal);
        var network = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await identityVerification.VerifyAsync(accountId, emailVerified, request, network, cancellationToken);
        if (result.ThrottleDelay > Duration.Zero) await Task.Delay(result.ThrottleDelay.ToTimeSpan(), cancellationToken);
        return result.Succeeded ? Ok(new { verified = true }) : BadRequest(new { error = IdentityVerificationService.GenericErrorCode });
    }
}
