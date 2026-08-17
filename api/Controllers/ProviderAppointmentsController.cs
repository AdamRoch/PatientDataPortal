using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/provider/appointments")]
[RequireRole(AppRole.Provider)]
public sealed class ProviderAppointmentsController(IProviderAppointmentsRepository appointments, IClock clock, IAuditWriter audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProviderAppointmentSchedule>> List(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var schedule = await appointments.ListAsync(userId, clock.GetCurrentInstant(), cancellationToken);
        if (schedule is not null)
            await audit.WriteAllowedAsync(new AuditEvent(userId.ToString(), "provider", "provider_appointment_schedule_viewed", "appointment", "own_provider_schedule", "allowed"), cancellationToken);
        return schedule is null ? NotFound() : Ok(schedule);
    }
}
