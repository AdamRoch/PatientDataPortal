using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Seeding;

/// <summary>Creates deterministic synthetic provider availability for benchmark-only environments.</summary>
public sealed class BenchmarkScheduleSeedGenerator
{
    public const int ProviderCount = 10;
    public const int SlotsPerProvider = 1_600;
    public const int TotalSlotCount = ProviderCount * SlotsPerProvider;
    public static readonly Guid DemoProviderId = IdFor("provider:1");
    private const int BusinessDaysPerProvider = 100;
    private const int SlotsPerBusinessDay = 16;

    public static BenchmarkScheduleSeedSummary DescribePlan() => new(ProviderCount, TotalSlotCount, new DateOnly(2030, 1, 7), BusinessDaysPerProvider, SlotsPerBusinessDay);

    public async Task<BenchmarkScheduleSeedSummary> SeedAsync(CancellationToken cancellationToken = default)
    {
        var plan = DescribePlan();
        var databaseUrl = RequiredEnvironment("DATABASE_URL");
        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(databaseUrl));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var provider in Providers(plan))
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO user_profiles (user_id, role, display_name, tz) VALUES (@user_id, 'provider', @name, 'America/Chicago') ON CONFLICT (user_id) DO UPDATE SET role = EXCLUDED.role, display_name = EXCLUDED.display_name, tz = EXCLUDED.tz", cancellationToken,
                ("user_id", provider.UserId), ("name", provider.Name));
            await ExecuteAsync(connection, transaction, "INSERT INTO providers (id, user_id, tz, slot_length_min) VALUES (@id, @user_id, 'America/Chicago', 30) ON CONFLICT (id) DO UPDATE SET tz = EXCLUDED.tz, slot_length_min = EXCLUDED.slot_length_min", cancellationToken,
                ("id", provider.Id), ("user_id", provider.UserId));
            await ExecuteAsync(connection, transaction, "INSERT INTO services (id, provider_id, name, active) VALUES (@id, @provider_id, 'Synthetic benchmark consultation', true) ON CONFLICT (id) DO UPDATE SET provider_id = EXCLUDED.provider_id, name = EXCLUDED.name, active = EXCLUDED.active", cancellationToken,
                ("id", provider.ServiceId), ("provider_id", provider.Id));

            await UpsertSlotsAsync(connection, transaction, provider, plan, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var verifiedProviders = await CountAsync(connection, "SELECT count(*) FROM providers WHERE id = ANY(@ids)", Providers(plan).Select(provider => provider.Id).ToArray(), cancellationToken);
        var verifiedSlots = await CountAsync(connection, "SELECT count(*) FROM slots WHERE id = ANY(@ids)", Providers(plan).SelectMany(provider => Slots(provider, plan)).Select(slot => slot.Id).ToArray(), cancellationToken);
        if (verifiedProviders != plan.Providers || verifiedSlots != plan.Slots)
            throw new InvalidOperationException($"Benchmark schedule seed verification failed: providers={verifiedProviders}/{plan.Providers} slots={verifiedSlots}/{plan.Slots}.");
        return plan with { VerifiedProviders = verifiedProviders, VerifiedSlots = verifiedSlots };
    }

    public static async Task WriteK6FixtureAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var plan = DescribePlan();
        var fixture = new BenchmarkK6Fixture(
            Providers(plan).Select(provider => new BenchmarkK6Provider(
                provider.Id,
                provider.ServiceId,
                Slots(provider, plan).Select(slot => slot.Id).ToArray())).ToArray());
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), cancellationToken);
        Console.WriteLine($"benchmark-k6-fixture path={destination} providers={fixture.Providers.Length} slots={fixture.Providers.Sum(provider => provider.SlotIds.Length)}");
    }

    internal static IEnumerable<SeedProvider> Providers(BenchmarkScheduleSeedSummary plan)
    {
        for (var number = 1; number <= plan.Providers; number++)
            yield return new(IdFor($"provider:{number}"), IdFor($"provider-user:{number}"), IdFor($"provider-service:{number}"), $"Synthetic Benchmark Provider {number:00}");
    }

    internal static IEnumerable<SeedSlot> Slots(SeedProvider provider, BenchmarkScheduleSeedSummary plan)
    {
        var date = plan.FirstBusinessDay;
        for (var day = 0; day < plan.BusinessDays; day++)
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { date = date.AddDays(1); day--; continue; }
            for (var slot = 0; slot < plan.SlotsPerBusinessDay; slot++)
            {
                var startsAt = new DateTimeOffset(date.Year, date.Month, date.Day, 8, 0, 0, TimeSpan.Zero).AddMinutes(slot * 30);
                yield return new(IdFor($"slot:{provider.Id}:{startsAt:O}"), startsAt, startsAt.AddMinutes(30));
            }
            date = date.AddDays(1);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSlotsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SeedProvider provider, BenchmarkScheduleSeedSummary plan, CancellationToken cancellationToken)
    {
        using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var slot in Slots(provider, plan))
        {
            var command = new NpgsqlBatchCommand("INSERT INTO slots (id, provider_id, start_at, end_at, status) VALUES (@id, @provider_id, @start_at, @end_at, 'open') ON CONFLICT (id) DO UPDATE SET provider_id = EXCLUDED.provider_id, start_at = EXCLUDED.start_at, end_at = EXCLUDED.end_at");
            command.Parameters.AddWithValue("id", slot.Id);
            command.Parameters.AddWithValue("provider_id", provider.Id);
            command.Parameters.AddWithValue("start_at", slot.StartsAt);
            command.Parameters.AddWithValue("end_at", slot.EndsAt);
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(NpgsqlConnection connection, string sql, Guid[] ids, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ids", ids);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static Guid IdFor(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes("PTDP-47:" + value)).AsSpan(0, 16));
    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"{name} must be set before running --seed-benchmark.");

    internal sealed record SeedProvider(Guid Id, Guid UserId, Guid ServiceId, string Name);
    internal sealed record SeedSlot(Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
}

public sealed record BenchmarkK6Fixture(BenchmarkK6Provider[] Providers);
public sealed record BenchmarkK6Provider(Guid ProviderId, Guid ServiceId, Guid[] SlotIds);

public sealed record BenchmarkScheduleSeedSummary(int Providers, int Slots, DateOnly FirstBusinessDay, int BusinessDays, int SlotsPerBusinessDay, int VerifiedProviders = 0, int VerifiedSlots = 0)
{
    public string ToLogLine() => string.Create(CultureInfo.InvariantCulture, $"benchmark-schedule-seed providers={Providers} slots={Slots} first_business_day={FirstBusinessDay:yyyy-MM-dd} business_days={BusinessDays} slots_per_business_day={SlotsPerBusinessDay} verified_providers={VerifiedProviders} verified_slots={VerifiedSlots}");
}
