using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Security;
using PatientDataPortal.Api.Studies;
using PatientDataPortal.Api.Imaging;

namespace PatientDataPortal.Api.Controllers;

// These route roots are protected now so later resource slices cannot accidentally expose an
// unverified patient while they add their resource-specific handlers beneath the same roots.
[ApiController]
[RequireRole(AppRole.Patient)]
[RequireVerifiedPatient]
public sealed class VerifiedPatientResourcesController : ControllerBase
{
    [HttpGet("api/studies")]
    public async Task<ActionResult<IReadOnlyList<StudyListItem>>> Studies(
        [FromServices] IStudyRepository studies,
        CancellationToken cancellationToken) => Ok(await studies.ListCompletedForPatientAsync(UserId(), cancellationToken));

    [HttpGet("api/images/{id:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ImageAccess>> Image(Guid id, [FromServices] IImageAccessService images, CancellationToken cancellationToken)
    {
        var image = await images.MintForPatientAsync(id, UserId(), cancellationToken);
        return image is null ? NotFound() : Ok(image);
    }

    [HttpGet("api/images")] public IActionResult Images() => NotFound();
    [HttpGet("api/cine")] public IActionResult Cine() => NotFound();
    [HttpGet("api/reports")] public IActionResult Reports() => NotFound();

    private Guid UserId() => Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
}
