using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Scheduling;

public sealed record DiscoverableProvider(Guid Id, string Name, IReadOnlyList<OfferedService> Services);
public sealed record OpenSlot(Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public interface IProviderDiscoveryRepository
{
    Task<IReadOnlyList<DiscoverableProvider>> ListProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<OpenSlot>> ListOpenSlotsAsync(Guid providerId, Instant from, Instant to, CancellationToken cancellationToken);
}

public sealed class ProviderDiscoveryRepository(IOptions<DatabaseOptions> databaseOptions, IClock clock) : IProviderDiscoveryRepository
{
    public async Task<IReadOnlyList<DiscoverableProvider>> ListProvidersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
            SELECT p.id, profile.display_name, s.id, s.name
            FROM providers p
            JOIN user_profiles profile ON profile.user_id = p.user_id
            JOIN services s ON s.provider_id = p.id AND s.active
            ORDER BY profile.display_name, s.name
            """;
        var providers = new Dictionary<Guid, (string Name, List<OfferedService> Services)>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            if (!providers.TryGetValue(id, out var provider)) provider = (reader.GetString(1), []);
            provider.Services.Add(new OfferedService(reader.GetGuid(2), reader.GetString(3), true));
            providers[id] = provider;
        }
        return providers.Select(pair => new DiscoverableProvider(pair.Key, pair.Value.Name, pair.Value.Services)).ToArray();
    }

    public async Task<IReadOnlyList<OpenSlot>> ListOpenSlotsAsync(Guid providerId, Instant from, Instant to, CancellationToken cancellationToken)
    {
        var futureFrom = Instant.Max(from, clock.GetCurrentInstant());
        await using var connection = await OpenAsync(cancellationToken);
        // This predicate intentionally matches slots_provider_open_start_idx.
        const string sql = "SELECT id, start_at, end_at FROM slots WHERE provider_id = $1 AND status = 'open' AND start_at >= $2 AND start_at < $3 ORDER BY start_at";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(providerId);
        command.Parameters.AddWithValue(futureFrom.ToDateTimeOffset());
        command.Parameters.AddWithValue(to.ToDateTimeOffset());
        var slots = new List<OpenSlot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            slots.Add(new OpenSlot(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2)));
        return slots;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var value = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Database is not configured.");
        var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(value));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
