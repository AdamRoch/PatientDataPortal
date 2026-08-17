using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PatientDataPortal.Api.Identity;

namespace PatientDataPortal.Api.Security;

public sealed class VerifiedPatientRequirement : IAuthorizationRequirement;

public sealed class VerifiedPatientAuthorizationHandler(
    IIdentityVerificationService identityVerification,
    IAuditWriter auditWriter,
    IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<VerifiedPatientRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, VerifiedPatientRequirement requirement)
    {
        var userIdText = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(userIdText, out var userId) || !await identityVerification.IsVerifiedPatientAsync(userId, CancellationToken.None))
        {
            await auditWriter.WriteDeniedAsync(new AuditEvent(userIdText, "patient", "verified_patient_required", "api_route", httpContextAccessor.HttpContext?.Request.Path.Value ?? "unknown", "denied"), CancellationToken.None);
            return;
        }
        context.Succeed(requirement);
    }
}
