using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Health;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class HealthServiceTests
{
    [Fact]
    public async Task MissingDependencies_ReturnsDegradedInsteadOfThrowing()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.Configure<DatabaseOptions>(_ => { });
        services.Configure<SupabaseOptions>(_ => { });
        services.AddScoped<HealthService>();
        await using var provider = services.BuildServiceProvider();

        var health = await provider.GetRequiredService<HealthService>().CheckAsync(CancellationToken.None);

        Assert.Equal("degraded", health.Status);
        Assert.Equal("unavailable", health.Database.Status);
        Assert.Equal("unavailable", health.Storage.Status);
    }
}
