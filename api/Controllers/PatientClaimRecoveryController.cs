using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

public sealed record PatientClaimRecoveryRequest(string? ReasonCode);

[ApiController]
[Route("api/admin/patient-claims")]
[RequireRole(AppRole.Admin)]
public sealed class PatientClaimRecoveryController(IIdentityVerificationService identityVerification) : ControllerBase
{
    [HttpPost("{patientRecordId:guid}/unlink")]
    public async Task<IActionResult> Unlink(Guid patientRecordId, [FromBody] PatientClaimRecoveryRequest request, CancellationToken cancellationToken)
    {
        await identityVerification.RecoverClaimAsync(patientRecordId, Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), request.ReasonCode?.Trim(), cancellationToken);
        return NoContent();
    }
}
