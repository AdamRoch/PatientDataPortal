using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Audit;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/audit-log")]
[RequireRole(AppRole.Admin, AppRole.Provider)]
public sealed class AuditLogController(IAuditLogRepository auditLog, IUserProfileRoleRepository profiles, IAuditWriter audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AuditLogItem>>> List([FromQuery] string? actor, [FromQuery] string? action, [FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = await profiles.GetRoleAsync(userId, cancellationToken);
        var filters = new AuditLogFilters(BlankToNull(actor), BlankToNull(action), date);
        if (role == AppRole.Admin)
        {
            var entries = await auditLog.ListForAdminAsync(filters, cancellationToken);
            await audit.WriteAllowedAsync(new AuditEvent(userId.ToString(), "admin", "audit_log_viewed", "audit_log", "all", "allowed"), cancellationToken);
            return Ok(entries);
        }

        if (role == AppRole.Provider)
        {
            var entries = await auditLog.ListForProviderAsync(userId, filters, cancellationToken);
            await audit.WriteAllowedAsync(new AuditEvent(userId.ToString(), "provider", "audit_log_viewed", "audit_log", "own_provider_patients", "allowed"), cancellationToken);
            return Ok(entries);
        }

        return Forbid();
    }

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
