using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Profiles;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/profile")]
[RequireRole(AppRole.Patient)]
public sealed class PatientProfileController(IPatientProfileRepository profiles) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PatientProfile>> Get(CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(UserId(), cancellationToken);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPut]
    public async Task<ActionResult<PatientProfile>> Put(UpdatePatientProfileRequest request, CancellationToken cancellationToken)
    {
        var displayName = request.DisplayName?.Trim();
        var timeZone = request.TimeZone?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 120)
            ModelState.AddModelError(nameof(request.DisplayName), "Display name must be between 1 and 120 characters.");
        if (string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > 64 || !IsKnownTimeZone(timeZone))
            ModelState.AddModelError(nameof(request.TimeZone), "Time zone must be a valid time zone identifier.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var profile = await profiles.UpdateAsync(UserId(), displayName!, timeZone!, cancellationToken);
        return profile is null ? NotFound() : Ok(profile);
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static bool IsKnownTimeZone(string value)
    {
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(value); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
