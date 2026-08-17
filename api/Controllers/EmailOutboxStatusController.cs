using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Email;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/admin/email-outbox")]
[RequireRole(AppRole.Admin)]
public sealed class EmailOutboxStatusController(IEmailOutboxStatusRepository outbox, IAuditWriter audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EmailOutboxStatusItem>>> Get(CancellationToken cancellationToken)
    {
        var rows = await outbox.ListAsync(cancellationToken);
        await audit.WriteAllowedAsync(new AuditEvent(UserId().ToString(), "admin", "email_outbox_status_view", "email_outbox", "all", "allowed"), cancellationToken);
        return Ok(rows);
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
