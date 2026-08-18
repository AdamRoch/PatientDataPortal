using Microsoft.AspNetCore.Mvc;
using PatientDataPortal.Api.Health;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController(HealthService healthService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HealthResponse>> Get(CancellationToken cancellationToken)
    {
        var health = await healthService.CheckAsync(cancellationToken);
        return health.Status == "healthy"
            ? Ok(health)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, health);
    }
}
