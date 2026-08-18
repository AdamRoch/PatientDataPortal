using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Email;

namespace PatientDataPortal.Api.Controllers;

[ApiController]
[Route("api/jobs/email-outbox")]
public sealed class EmailOutboxJobController(IOptions<OutboxOptions> options, IEmailOutboxProcessor worker) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<EmailOutboxRunResult>> Post(CancellationToken cancellationToken)
    {
        var expected = options.Value.JobSecret;
        var supplied = Request.Headers["X-Outbox-Job-Secret"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || !SecretsMatch(expected, supplied)) return Unauthorized();

        return Ok(await worker.ProcessAsync(cancellationToken));
    }

    private static bool SecretsMatch(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
