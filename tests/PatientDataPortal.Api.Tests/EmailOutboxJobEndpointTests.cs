using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class EmailOutboxJobEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task PostWithoutSharedSecret_IsRejectedBeforeTheWorkerRuns()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/jobs/email-outbox", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
