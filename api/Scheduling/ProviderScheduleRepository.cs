using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Scheduling;

public sealed class ProviderScheduleRepository(IOptions<DatabaseOptions> databaseOptions) : IProviderScheduleRepository
{
    public async Task<ProviderSchedule?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var providerId = await ProviderIdAsync(connection, null, userId, cancellationToken);
        return providerId is null ? null : await ReadAsync(connection, null, providerId.Value, cancellationToken);
    }

    public Task<ProviderSchedule?> ReplaceWorkingHoursAsync(Guid userId, IReadOnlyList<WorkingHoursInput> rules, DateOnly today, CancellationToken cancellationToken) =>
        MutateAsync(userId, async (connection, transaction, providerId) =>
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM availability_rules WHERE provider_id = $1", cancellationToken, providerId);
            foreach (var rule in rules)
                await ExecuteAsync(connection, transaction, "INSERT INTO availability_rules (id, provider_id, weekday, local_start, local_end, effective_from, effective_until) VALUES ($1, $2, $3, $4, $5, $6, $7)", cancellationToken, Guid.NewGuid(), providerId, (short)rule.Weekday, rule.LocalStart, rule.LocalEnd, rule.EffectiveFrom ?? today, rule.EffectiveUntil);
            return await ReadAsync(connection, transaction, providerId, cancellationToken);
        }, cancellationToken);

    public Task<ProviderSchedule?> UpdateSlotLengthAsync(Guid userId, int slotLengthMinutes, CancellationToken cancellationToken) =>
        MutateAsync(userId, async (connection, transaction, providerId) =>
        {
            await ExecuteAsync(connection, transaction, "UPDATE providers SET slot_length_min = $1 WHERE id = $2", cancellationToken, slotLengthMinutes, providerId);
            return await ReadAsync(connection, transaction, providerId, cancellationToken);
        }, cancellationToken);

    public Task<BlockedTime?> CreateBlockedTimeAsync(Guid userId, Instant startsAt, Instant endsAt, CancellationToken cancellationToken) =>
        MutateAsync(userId, async (connection, transaction, providerId) =>
        {
            var id = Guid.NewGuid();
            await ExecuteAsync(connection, transaction, "INSERT INTO blocked_times (id, provider_id, blocked_range) VALUES ($1, $2, tstzrange($3, $4, '[)'))", cancellationToken, id, providerId, startsAt.ToDateTimeOffset(), endsAt.ToDateTimeOffset());
            return new BlockedTime(id, startsAt, endsAt);
        }, cancellationToken);

    public Task<BlockedTime?> UpdateBlockedTimeAsync(Guid userId, Guid blockedTimeId, Instant startsAt, Instant endsAt, CancellationToken cancellationToken) =>
        MutateAsync(userId, async (connection, transaction, providerId) =>
        {
            var count = await ExecuteAsync(connection, transaction, "UPDATE blocked_times SET blocked_range = tstzrange($1, $2, '[)') WHERE id = $3 AND provider_id = $4", cancellationToken, startsAt.ToDateTimeOffset(), endsAt.ToDateTimeOffset(), blockedTimeId, providerId);
            return count == 0 ? null : new BlockedTime(blockedTimeId, startsAt, endsAt);
        }, cancellationToken);

    public Task<bool?> DeleteBlockedTimeAsync(Guid userId, Guid blockedTimeId, CancellationToken cancellationToken) =>
        MutateAsync(userId, async (connection, transaction, providerId) =>
            await ExecuteAsync(connection, transaction, "DELETE FROM blocked_times WHERE id = $1 AND provider_id = $2", cancellationToken, blockedTimeId, providerId) > 0, cancellationToken);

    public Task<OfferedService?> CreateServiceAsync(Guid userId, string name, bool active, CancellationToken cancellationToken) =>
        MutateAsync(userId, async (connection, transaction, providerId) =>
        {
            var id = Guid.NewGuid();
            await ExecuteAsync(connection, transaction, "INSERT INTO services (id, provider_id, name, active) VALUES ($1, $2, $3, $4)", cancellationToken, id, providerId, name, active);
            return new OfferedService(id, name, active);
        }, cancellationToken);

    public Task<OfferedService?> UpdateServiceAsync(Guid userId, Guid serviceId, string name, bool active, CancellationToken cancellationToken) =>
        MutateAsync(userId, async (connection, transaction, providerId) =>
        {
            var count = await ExecuteAsync(connection, transaction, "UPDATE services SET name = $1, active = $2 WHERE id = $3 AND provider_id = $4", cancellationToken, name, active, serviceId, providerId);
            return count == 0 ? null : new OfferedService(serviceId, name, active);
        }, cancellationToken);

    public Task<bool?> DeleteServiceAsync(Guid userId, Guid serviceId, CancellationToken cancellationToken) =>
        MutateAsync(userId, async (connection, transaction, providerId) =>
            await ExecuteAsync(connection, transaction, "DELETE FROM services WHERE id = $1 AND provider_id = $2", cancellationToken, serviceId, providerId) > 0, cancellationToken);

    private async Task<T?> MutateAsync<T>(Guid userId, Func<NpgsqlConnection, NpgsqlTransaction, Guid, Task<T>> operation, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var providerId = await ProviderIdAsync(connection, transaction, userId, cancellationToken);
        if (providerId is null) { await transaction.RollbackAsync(cancellationToken); return default; }
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))", cancellationToken, providerId.Value);
        var result = await operation(connection, transaction, providerId.Value);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<bool?> MutateAsync(Guid userId, Func<NpgsqlConnection, NpgsqlTransaction, Guid, Task<bool>> operation, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var providerId = await ProviderIdAsync(connection, transaction, userId, cancellationToken);
        if (providerId is null) { await transaction.RollbackAsync(cancellationToken); return null; }
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))", cancellationToken, providerId.Value);
        var result = await operation(connection, transaction, providerId.Value);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<ProviderSchedule> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid providerId, CancellationToken cancellationToken)
    {
        var slotLength = (int)(await ScalarAsync(connection, transaction, "SELECT slot_length_min FROM providers WHERE id = $1", cancellationToken, providerId))!;
        var rules = new List<WorkingHours>();
        await using (var command = Command(connection, transaction, "SELECT id, weekday, local_start, local_end, effective_from, effective_until FROM availability_rules WHERE provider_id = $1 ORDER BY weekday, local_start", providerId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) rules.Add(new(reader.GetGuid(0), reader.GetInt16(1), reader.GetFieldValue<TimeOnly>(2), reader.GetFieldValue<TimeOnly>(3), reader.GetFieldValue<DateOnly>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5)));
        var blocked = new List<BlockedTime>();
        await using (var command = Command(connection, transaction, "SELECT id, lower(blocked_range), upper(blocked_range) FROM blocked_times WHERE provider_id = $1 ORDER BY lower(blocked_range)", providerId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) blocked.Add(new(reader.GetGuid(0), Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(1)), Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(2))));
        var services = new List<OfferedService>();
        await using (var command = Command(connection, transaction, "SELECT id, name, active FROM services WHERE provider_id = $1 ORDER BY name", providerId))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) services.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));
        return new(slotLength, rules, blocked, services);
    }

    private static async Task<Guid?> ProviderIdAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, transaction, "SELECT id FROM providers WHERE user_id = $1", cancellationToken, userId);
        return value is Guid id ? id : null;
    }
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var value = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Database is not configured.");
        var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(value));
        await connection.OpenAsync(cancellationToken); return connection;
    }
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        return command;
    }
    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object?[] values)
    { await using var command = Command(connection, transaction, sql, values); return await command.ExecuteNonQueryAsync(cancellationToken); }
    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] values)
    { await using var command = Command(connection, transaction, sql, values); return await command.ExecuteScalarAsync(cancellationToken); }
}
