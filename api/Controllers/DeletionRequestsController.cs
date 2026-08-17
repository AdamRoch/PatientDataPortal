using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Deletion;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
public sealed class DeletionRequestsController(IDeletionRequestService requests, IAuditWriter audit) : ControllerBase
{
    [HttpPost("api/deletion-requests")]
    [RequireRole(AppRole.Patient)]
    public async Task<ActionResult<DeletionRequest>> Submit(CancellationToken cancellationToken)
    {
        var result = await requests.RequestAsync(UserId(), cancellationToken);
        if (result is null) return NotFound();
        await audit.WriteAllowedAsync(new AuditEvent(null, "patient", "deletion_requested", "deletion_request", result.AuditReference.ToString(), "allowed"), cancellationToken);
        return Created($"/api/deletion-requests/{result.Id}", result);
    }

    [HttpGet("api/admin/deletion-requests")]
    [RequireRole(AppRole.Admin)]
    public async Task<ActionResult<IReadOnlyList<AdminDeletionRequest>>> List(CancellationToken cancellationToken)
    {
        var result = await requests.ListPendingAsync(cancellationToken);
        await audit.WriteAllowedAsync(new AuditEvent(UserId().ToString(), "admin", "deletion_requests_view", "deletion_request", "pending", "allowed"), cancellationToken);
        return Ok(result);
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
