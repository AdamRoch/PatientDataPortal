using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Health;

public sealed class HealthService(
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<SupabaseOptions> supabaseOptions,
    IHttpClientFactory httpClientFactory)
{
    public async Task<HealthResponse> CheckAsync(CancellationToken cancellationToken)
    {
        var app = new DependencyHealth("healthy", 0);
        var database = await CheckDatabaseAsync(cancellationToken);
        var storage = await CheckStorageAsync(cancellationToken);
        var overall = database.Status == "healthy" && storage.Status == "healthy"
            ? "healthy"
            : "degraded";

        return new HealthResponse(overall, app, database, storage);
    }

    private async Task<DependencyHealth> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseOptions.Value.ConnectionString))
        {
            return new DependencyHealth("unavailable", 0);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(
                DatabaseConnectionString.Normalize(databaseOptions.Value.ConnectionString));
            await using var command = dataSource.CreateCommand("select 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return new DependencyHealth("healthy", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is NpgsqlException or ArgumentException)
        {
            return new DependencyHealth("unavailable", stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<DependencyHealth> CheckStorageAsync(CancellationToken cancellationToken)
    {
        var options = supabaseOptions.Value;
        if (string.IsNullOrWhiteSpace(options.Url) || string.IsNullOrWhiteSpace(options.ServiceKey))
        {
            return new DependencyHealth("unavailable", 0);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = httpClientFactory.CreateClient(nameof(HealthService));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Url.TrimEnd('/')}/storage/v1/bucket");
            request.Headers.Add("apikey", options.ServiceKey);
            request.Headers.Authorization = new("Bearer", options.ServiceKey);
            using var response = await client.SendAsync(request, cancellationToken);
            return new DependencyHealth(response.IsSuccessStatusCode ? "healthy" : "unavailable", stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            return new DependencyHealth("unavailable", stopwatch.ElapsedMilliseconds);
        }
    }
}
