using Microsoft.AspNetCore.Mvc;
using NodaTime;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/providers")]
[RequireRole(AppRole.Patient)]
public sealed class ProviderDiscoveryController(IProviderDiscoveryRepository providers) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DiscoverableProvider>>> List(CancellationToken cancellationToken) =>
        Ok(await providers.ListProvidersAsync(cancellationToken));

    [HttpGet("{id:guid}/slots")]
    public async Task<ActionResult<IReadOnlyList<OpenSlot>>> Slots(Guid id, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (from is null || to is null) return BadRequest(new { error = "from_and_to_required" });
        var startsAt = Instant.FromDateTimeOffset(from.Value);
        var endsAt = Instant.FromDateTimeOffset(to.Value);
        if (endsAt <= startsAt || endsAt - startsAt > Duration.FromDays(31)) return BadRequest(new { error = "invalid_slot_range" });
        return Ok(await providers.ListOpenSlotsAsync(id, startsAt, endsAt, cancellationToken));
    }
}
