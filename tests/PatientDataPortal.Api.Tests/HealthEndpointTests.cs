using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetHealth_IsAnonymous_DoesNotExposeSensitiveConfiguration_AndRejectsDegradedDependencies()
    {
        await using var application = factory
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DATABASE_URL"] = string.Empty,
                    ["SUPABASE_URL"] = string.Empty,
                    ["SUPABASE_SERVICE_KEY"] = string.Empty,
                })));
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var requestIds));
        Assert.NotEmpty(requestIds);
        Assert.Contains("\"app\"", body);
        Assert.Contains("\"database\"", body);
        Assert.Contains("\"storage\"", body);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }
}
