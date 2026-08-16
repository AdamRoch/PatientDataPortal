using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Health;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class SupabaseProjectContractTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SessionPoolerAndPrivateStorageAreReachable()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_SUPABASE_CONTRACT_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var databaseUrl = RequireEnvironment("DATABASE_URL");
        var supabaseUrl = RequireEnvironment("SUPABASE_URL");
        var serviceKey = RequireEnvironment("SUPABASE_SERVICE_KEY");
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.Configure<DatabaseOptions>(options => options.ConnectionString = databaseUrl);
        services.Configure<SupabaseOptions>(options =>
        {
            options.Url = supabaseUrl;
            options.ServiceKey = serviceKey;
        });
        services.AddScoped<HealthService>();
        await using var provider = services.BuildServiceProvider();

        var health = await provider.GetRequiredService<HealthService>().CheckAsync(CancellationToken.None);

        Assert.Equal("healthy", health.Database.Status);
        Assert.Equal("healthy", health.Storage.Status);
    }

    private static string RequireEnvironment(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{key} must be set for the Supabase contract check.");
}
