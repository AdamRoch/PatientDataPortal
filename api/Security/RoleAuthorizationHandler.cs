using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace PatientDataPortal.Api.Security;

public sealed class RoleAuthorizationHandler(
    IUserProfileRoleRepository profiles,
    IAuditWriter auditWriter,
    IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<RoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        var userIdText = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(userIdText, out var userId)) return;

        var request = httpContextAccessor.HttpContext?.Request;
        var role = await profiles.GetRoleAsync(userId, CancellationToken.None);
        if (role is { } resolvedRole && requirement.AllowedRoles.Contains(resolvedRole))
        {
            context.Succeed(requirement);
            return;
        }

        await auditWriter.WriteDeniedAsync(new AuditEvent(
            userId.ToString(),
            role?.ToString().ToLowerInvariant() ?? "anonymous",
            "authorization_denied",
            "api_route",
            request?.Path.Value ?? "unknown",
            "denied"), CancellationToken.None);
    }
}
