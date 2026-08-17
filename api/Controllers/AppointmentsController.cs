using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/appointments")]
[RequireRole(AppRole.Patient)]
public sealed class AppointmentsController(IAppointmentBookingService bookings, IAppointmentChangeService changes) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<AppointmentConfirmation>> Create(CreateAppointmentRequest request, CancellationToken cancellationToken)
    {
        if (request.SlotId == Guid.Empty || request.ServiceId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            return BadRequest(new { error = "invalid_appointment_request" });

        var stopwatch = Stopwatch.StartNew();
        var confirmation = await bookings.BookAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), request with { IdempotencyKey = request.IdempotencyKey.Trim() }, cancellationToken);
        Response.Headers["Server-Timing"] = $"booking;dur={Math.Min(stopwatch.Elapsed.TotalMilliseconds, 9999):F1}";
        return Created($"/api/appointments/{confirmation.Id}", confirmation);
    }

    [HttpPost("{appointmentId:guid}/reschedule")]
    public async Task<ActionResult<AppointmentChangeConfirmation>> Reschedule(Guid appointmentId, RescheduleAppointmentRequest request, CancellationToken cancellationToken)
    {
        if (request.SlotId == Guid.Empty) return BadRequest(new { error = "invalid_reschedule_request" });
        return Ok(await changes.RescheduleAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), appointmentId, request, cancellationToken));
    }

    [HttpDelete("{appointmentId:guid}")]
    public async Task<IActionResult> Cancel(Guid appointmentId, CancellationToken cancellationToken)
    {
        await changes.CancelAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), appointmentId, cancellationToken);
        return NoContent();
    }
}
