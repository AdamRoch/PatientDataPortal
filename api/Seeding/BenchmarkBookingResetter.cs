using System.Globalization;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Seeding;

/// <summary>Restores only deterministic benchmark-provider slots after a load run.</summary>
public sealed class BenchmarkBookingResetter
{
    public async Task<BenchmarkBookingResetSummary> ResetAsync(CancellationToken cancellationToken = default)
    {
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? throw new InvalidOperationException("DATABASE_URL must be set before running --reset-benchmark-bookings.");
        var plan = BenchmarkScheduleSeedGenerator.DescribePlan();
        var providerIds = BenchmarkScheduleSeedGenerator.Providers(plan).Select(provider => provider.Id).ToArray();

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(databaseUrl));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var appointments = await CountAsync(connection, transaction, "SELECT count(*) FROM appointments WHERE provider_id = ANY(@provider_ids)", providerIds, cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM email_outbox WHERE appointment_id IN (SELECT id FROM appointments WHERE provider_id = ANY(@provider_ids))", providerIds, cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM appointment_events WHERE appointment_id IN (SELECT id FROM appointments WHERE provider_id = ANY(@provider_ids))", providerIds, cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM appointments WHERE provider_id = ANY(@provider_ids)", providerIds, cancellationToken);
        var reopenedSlots = await ExecuteAsync(connection, transaction, "UPDATE slots SET status = 'open' WHERE provider_id = ANY(@provider_ids) AND status = 'booked'", providerIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BenchmarkBookingResetSummary(appointments, reopenedSlots);
    }

    private static async Task<int> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid[] providerIds, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("provider_ids", providerIds);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid[] providerIds, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("provider_ids", providerIds);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record BenchmarkBookingResetSummary(int DeletedAppointments, int ReopenedSlots)
{
    public string ToLogLine() => $"benchmark-booking-reset deleted_appointments={DeletedAppointments} reopened_slots={ReopenedSlots}";
}
