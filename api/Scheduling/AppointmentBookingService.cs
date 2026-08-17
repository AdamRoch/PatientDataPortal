using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Errors;

namespace PatientDataPortal.Api.Scheduling;

public sealed record CreateAppointmentRequest(Guid SlotId, Guid ServiceId, string IdempotencyKey);
public sealed record AppointmentConfirmation(Guid Id, Guid SlotId, Guid ProviderId, Guid ServiceId, DateTimeOffset StartsAt, int ScheduleVersion, string Status);

public interface IAppointmentBookingService
{
    Task<AppointmentConfirmation> BookAsync(Guid patientUserId, CreateAppointmentRequest request, CancellationToken cancellationToken);
}

public sealed class AppointmentBookingService(IOptions<DatabaseOptions> databaseOptions, IClock clock) : IAppointmentBookingService
{
    public async Task<AppointmentConfirmation> BookAsync(Guid patientUserId, CreateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for appointment booking.");

        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var existing = await FindByIdempotencyKeyAsync(connection, transaction, patientUserId, request.IdempotencyKey, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            var providerId = await FindSlotProviderAsync(connection, transaction, request.SlotId, cancellationToken)
                ?? throw new DomainException("slot_no_longer_available", "The selected slot is no longer available.", StatusCodes.Status409Conflict);
            await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))", cancellationToken, providerId);

            // A concurrent retry may have been waiting on this provider's lock.
            existing = await FindByIdempotencyKeyAsync(connection, transaction, patientUserId, request.IdempotencyKey, cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            if (!await IsProviderServiceAsync(connection, transaction, providerId, request.ServiceId, cancellationToken))
                throw new DomainException("invalid_service", "The selected service is unavailable.");

            var startsAt = await ClaimOpenSlotAsync(connection, transaction, request.SlotId, providerId, cancellationToken);
            if (startsAt is null) throw new DomainException("slot_no_longer_available", "The selected slot is no longer available.", StatusCodes.Status409Conflict);

            var appointmentId = Guid.NewGuid();
            var now = clock.GetCurrentInstant().ToDateTimeOffset();
            await InsertAppointmentAsync(connection, transaction, appointmentId, request, patientUserId, providerId, startsAt.Value, now, cancellationToken);
            await InsertEventAsync(connection, transaction, appointmentId, null, "requested", patientUserId, now, cancellationToken);
            await ExecuteAsync(connection, transaction, "UPDATE appointments SET status = 'confirmed', updated_at = $1 WHERE id = $2", cancellationToken, now, appointmentId);
            await InsertEventAsync(connection, transaction, appointmentId, "requested", "confirmed", patientUserId, now, cancellationToken);
            await InsertReminderAsync(connection, transaction, appointmentId, startsAt.Value, cancellationToken);
            await InsertAuditAsync(connection, transaction, patientUserId, appointmentId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AppointmentConfirmation(appointmentId, request.SlotId, providerId, request.ServiceId, startsAt.Value, 1, "confirmed");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The database indexes remain the authority if requests reach different locks concurrently.
            var existing = await FindByIdempotencyKeyAsync(connection, null, patientUserId, request.IdempotencyKey, cancellationToken);
            if (existing is not null) return existing;
            if (exception.ConstraintName is "appointments_active_slot_unique" or "appointments_active_provider_start_unique")
                throw new DomainException("slot_no_longer_available", "The selected slot is no longer available.", StatusCodes.Status409Conflict);
            throw;
        }
    }

    private static async Task<AppointmentConfirmation?> FindByIdempotencyKeyAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid patientUserId, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id, slot_id, provider_id, service_id, start_at, schedule_version, status FROM appointments WHERE patient_user_id = $1 AND idempotency_key = $2", connection, transaction);
        command.Parameters.AddWithValue(patientUserId); command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetInt32(5), reader.GetString(6))
            : null;
    }

    private static async Task<Guid?> FindSlotProviderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid slotId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT provider_id FROM slots WHERE id = $1", connection, transaction);
        command.Parameters.AddWithValue(slotId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid providerId ? providerId : null;
    }

    private static async Task<bool> IsProviderServiceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid providerId, Guid serviceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM services WHERE id = $1 AND provider_id = $2 AND active)", connection, transaction);
        command.Parameters.AddWithValue(serviceId); command.Parameters.AddWithValue(providerId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<DateTimeOffset?> ClaimOpenSlotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid slotId, Guid providerId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE slots SET status = 'booked' WHERE id = $1 AND provider_id = $2 AND status = 'open' RETURNING start_at", connection, transaction);
        command.Parameters.AddWithValue(slotId); command.Parameters.AddWithValue(providerId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DateTimeOffset startsAt ? startsAt : null;
    }

    private static Task InsertAppointmentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid appointmentId, CreateAppointmentRequest request, Guid patientUserId, Guid providerId, DateTimeOffset startsAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO appointments (id, slot_id, patient_user_id, provider_id, service_id, start_at, schedule_version, status, idempotency_key, created_at, updated_at) VALUES ($1, $2, $3, $4, $5, $6, 1, 'requested', $7, $8, $8)", cancellationToken, appointmentId, request.SlotId, patientUserId, providerId, request.ServiceId, startsAt, request.IdempotencyKey, now);

    private static Task InsertEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid appointmentId, string? from, string to, Guid patientUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO appointment_events (id, appointment_id, from_status, to_status, actor_user_id, actor_role, occurred_at) VALUES ($1, $2, $3, $4, $5, 'patient', $6)", cancellationToken, Guid.NewGuid(), appointmentId, (object?)from ?? DBNull.Value, to, patientUserId, now);

    private static Task InsertReminderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid appointmentId, DateTimeOffset startsAt, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO email_outbox (id, appointment_id, schedule_version, interval, kind, payload, due_at, status, idempotency_key) VALUES ($1, $2, 1, '24h', 'reminder', CAST($3 AS jsonb), $4 - INTERVAL '24 hours', 'pending', $5)", cancellationToken, Guid.NewGuid(), appointmentId, JsonSerializer.Serialize(new { appointmentId, scheduleVersion = 1, interval = "24h" }), startsAt, $"appointment/{appointmentId}/1/24h");

    private static Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid patientUserId, Guid appointmentId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result, occurred_at) VALUES ($1, $2, 'patient', 'appointment_booked', 'appointment', $3, 'allowed', $4)", cancellationToken, Guid.NewGuid(), patientUserId.ToString(), appointmentId.ToString(), now);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
