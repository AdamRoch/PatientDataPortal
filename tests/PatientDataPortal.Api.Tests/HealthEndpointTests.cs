using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetHealth_IsAnonymousAndDoesNotExposeSensitiveConfiguration()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"app\"", body);
        Assert.Contains("\"database\"", body);
        Assert.Contains("\"storage\"", body);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }
}
