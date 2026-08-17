using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Errors;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/appointments")]
[RequireRole(AppRole.Provider, AppRole.Admin)]
public sealed class AppointmentStatusController(IAppointmentLifecycleService lifecycle, IUserProfileRoleRepository profiles) : ControllerBase
{
    [HttpPatch("{appointmentId:guid}/status")]
    public async Task<ActionResult<AppointmentStatusConfirmation>> UpdateStatus(Guid appointmentId, AppointmentStatusRequest request, CancellationToken cancellationToken)
    {
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var role = await profiles.GetRoleAsync(actorId, cancellationToken)
            ?? throw new DomainException("appointment_status_forbidden", "Only providers and administrators can change appointment status.", StatusCodes.Status403Forbidden);
        return Ok(await lifecycle.TransitionAsync(actorId, role, appointmentId, request?.Status?.Trim().ToLowerInvariant() ?? string.Empty, cancellationToken));
    }
}
