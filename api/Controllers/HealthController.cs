using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Health;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController(HealthService healthService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<HealthResponse>> Get(CancellationToken cancellationToken) =>
        Ok(await healthService.CheckAsync(cancellationToken));
}
