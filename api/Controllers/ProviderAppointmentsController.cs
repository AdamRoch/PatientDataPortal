using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/provider/appointments")]
[RequireRole(AppRole.Provider)]
public sealed class ProviderAppointmentsController(IProviderAppointmentsRepository appointments, IClock clock) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProviderAppointmentSchedule>> List(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var schedule = await appointments.ListAsync(userId, clock.GetCurrentInstant(), cancellationToken);
        return schedule is null ? NotFound() : Ok(schedule);
    }
}
