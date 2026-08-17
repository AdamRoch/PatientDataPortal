using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using PatientDataPortal.Api.Errors;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/provider/schedule")]
[RequireRole(AppRole.Provider)]
public sealed class ProviderScheduleController(IProviderScheduleRepository schedules, IClock clock) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProviderSchedule>> Get(CancellationToken cancellationToken) =>
        (await schedules.GetAsync(UserId(), cancellationToken)) is { } schedule ? Ok(schedule) : NotFound();

    [HttpPut("working-hours")]
    public async Task<ActionResult<ProviderSchedule>> ReplaceWorkingHours(ReplaceWorkingHoursRequest request, CancellationToken cancellationToken)
    {
        var rules = request.Rules;
        if (rules is null || rules.Count > 7 || rules.GroupBy(rule => rule.Weekday).Any(group => group.Count() > 1) || rules.Any(rule => rule.Weekday is < 0 or > 6 || rule.LocalEnd <= rule.LocalStart || (rule.EffectiveUntil is not null && rule.EffectiveFrom is not null && rule.EffectiveUntil < rule.EffectiveFrom)))
            return BadRequest(new { error = "invalid_working_hours" });
        var today = DateOnly.FromDateTime(clock.GetCurrentInstant().InUtc().ToDateTimeUtc());
        try
        {
            var schedule = await schedules.ReplaceWorkingHoursAsync(UserId(), rules, today, cancellationToken);
            return schedule is null ? NotFound() : Ok(schedule);
        }
        catch (DomainException exception) when (exception.Code == "availability_conflict")
        {
            return Conflict(new { error = exception.Code, message = exception.Message });
        }
    }

    [HttpPut("slot-length")]
    public async Task<ActionResult<ProviderSchedule>> UpdateSlotLength(UpdateSlotLengthRequest request, CancellationToken cancellationToken)
    {
        if (request.SlotLengthMinutes is < 5 or > 480) return BadRequest(new { error = "invalid_slot_length" });
        var schedule = await schedules.UpdateSlotLengthAsync(UserId(), request.SlotLengthMinutes, cancellationToken);
        return schedule is null ? NotFound() : Ok(schedule);
    }

    [HttpPost("blocked-times")]
    public async Task<ActionResult<BlockedTime>> CreateBlockedTime(BlockedTimeRequest request, CancellationToken cancellationToken)
    {
        if (!ValidFutureRange(request, out var startsAt, out var endsAt)) return BadRequest(new { error = "invalid_blocked_time" });
        try
        {
            var blocked = await schedules.CreateBlockedTimeAsync(UserId(), startsAt, endsAt, cancellationToken);
            return blocked is null ? NotFound() : CreatedAtAction(nameof(Get), blocked);
        }
        catch (DomainException exception) when (exception.Code == "availability_conflict")
        {
            return Conflict(new { error = exception.Code, message = exception.Message });
        }
    }

    [HttpPut("blocked-times/{id:guid}")]
    public async Task<ActionResult<BlockedTime>> UpdateBlockedTime(Guid id, BlockedTimeRequest request, CancellationToken cancellationToken)
    {
        if (!ValidFutureRange(request, out var startsAt, out var endsAt)) return BadRequest(new { error = "invalid_blocked_time" });
        try
        {
            var blocked = await schedules.UpdateBlockedTimeAsync(UserId(), id, startsAt, endsAt, cancellationToken);
            return blocked is null ? NotFound() : Ok(blocked);
        }
        catch (DomainException exception) when (exception.Code == "availability_conflict")
        {
            return Conflict(new { error = exception.Code, message = exception.Message });
        }
    }

    [HttpDelete("blocked-times/{id:guid}")]
    public async Task<IActionResult> DeleteBlockedTime(Guid id, CancellationToken cancellationToken) =>
        await schedules.DeleteBlockedTimeAsync(UserId(), id, cancellationToken) is true ? NoContent() : NotFound();

    [HttpPost("services")]
    public async Task<ActionResult<OfferedService>> CreateService(CreateServiceRequest request, CancellationToken cancellationToken)
    {
        if (!ValidServiceName(request.Name, out var name)) return BadRequest(new { error = "invalid_service_name" });
        var service = await schedules.CreateServiceAsync(UserId(), name, request.Active ?? true, cancellationToken);
        return service is null ? NotFound() : CreatedAtAction(nameof(Get), service);
    }

    [HttpPut("services/{id:guid}")]
    public async Task<ActionResult<OfferedService>> UpdateService(Guid id, UpdateServiceRequest request, CancellationToken cancellationToken)
    {
        if (!ValidServiceName(request.Name, out var name) || request.Active is null) return BadRequest(new { error = "invalid_service" });
        var service = await schedules.UpdateServiceAsync(UserId(), id, name, request.Active.Value, cancellationToken);
        return service is null ? NotFound() : Ok(service);
    }

    [HttpDelete("services/{id:guid}")]
    public async Task<IActionResult> DeleteService(Guid id, CancellationToken cancellationToken) =>
        await schedules.DeleteServiceAsync(UserId(), id, cancellationToken) is true ? NoContent() : NotFound();

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool ValidFutureRange(BlockedTimeRequest request, out Instant startsAt, out Instant endsAt)
    {
        startsAt = Instant.FromDateTimeOffset(request.StartsAt); endsAt = Instant.FromDateTimeOffset(request.EndsAt);
        return endsAt > startsAt && startsAt > clock.GetCurrentInstant();
    }
    private static bool ValidServiceName(string? value, out string name)
    {
        name = value?.Trim() ?? string.Empty;
        return name.Length is > 0 and <= 120;
    }
}
