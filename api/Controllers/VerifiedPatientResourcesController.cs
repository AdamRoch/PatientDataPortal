using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Controllers;

// These route roots are protected now so later resource slices cannot accidentally expose an
// unverified patient while they add their resource-specific handlers beneath the same roots.
[ApiController]
[RequireRole(AppRole.Patient)]
[RequireVerifiedPatient]
public sealed class VerifiedPatientResourcesController : ControllerBase
{
    [HttpGet("api/studies")] public IActionResult Studies() => NotFound();
    [HttpGet("api/images")] public IActionResult Images() => NotFound();
    [HttpGet("api/cine")] public IActionResult Cine() => NotFound();
    [HttpGet("api/reports")] public IActionResult Reports() => NotFound();
}
