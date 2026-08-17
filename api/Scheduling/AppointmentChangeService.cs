using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Errors;

namespace PatientDataPortal.Api.Scheduling;

public sealed record RescheduleAppointmentRequest(Guid SlotId);
public sealed record AppointmentChangeConfirmation(Guid Id, Guid SlotId, DateTimeOffset StartsAt, int ScheduleVersion, string Status);

public interface IAppointmentChangeService
{
    Task<AppointmentChangeConfirmation> RescheduleAsync(Guid patientUserId, Guid appointmentId, RescheduleAppointmentRequest request, CancellationToken cancellationToken);
    Task CancelAsync(Guid patientUserId, Guid appointmentId, CancellationToken cancellationToken);
}

public sealed class AppointmentChangeService(IOptions<DatabaseOptions> databaseOptions, IOptions<ReminderOptions> reminderOptions, IClock clock) : IAppointmentChangeService
{
    public Task<AppointmentChangeConfirmation> RescheduleAsync(Guid patientUserId, Guid appointmentId, RescheduleAppointmentRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(patientUserId, appointmentId, request.SlotId, cancellationToken);

    public async Task CancelAsync(Guid patientUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var appointment = await FindOwnedConfirmedAppointmentAsync(connection, transaction, patientUserId, appointmentId, cancellationToken);
        EnsureMinimumNotice(appointment.StartsAt);
        await LockProviderAsync(connection, transaction, appointment.ProviderId, cancellationToken);

        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        await ExecuteAsync(connection, transaction, "UPDATE slots SET status = 'open' WHERE id = $1 AND provider_id = $2 AND status = 'booked'", cancellationToken, appointment.SlotId, appointment.ProviderId);
        await ExecuteAsync(connection, transaction, "UPDATE appointments SET status = 'cancelled', updated_at = $1 WHERE id = $2", cancellationToken, now, appointment.Id);
        await ExecuteAsync(connection, transaction, "INSERT INTO appointment_events (id, appointment_id, from_status, to_status, actor_user_id, actor_role, occurred_at) VALUES ($1, $2, 'confirmed', 'cancelled', $3, 'patient', $4)", cancellationToken, Guid.NewGuid(), appointment.Id, patientUserId, now);
        await ExecuteAsync(connection, transaction, "INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result, occurred_at) VALUES ($1, $2, 'patient', 'appointment_cancelled', 'appointment', $3, 'allowed', $4)", cancellationToken, Guid.NewGuid(), patientUserId.ToString(), appointment.Id.ToString(), now);
        await SupersedePendingRemindersAsync(connection, transaction, appointment.Id, appointment.ScheduleVersion, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<AppointmentChangeConfirmation> ChangeAsync(Guid patientUserId, Guid appointmentId, Guid newSlotId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var appointment = await FindOwnedConfirmedAppointmentAsync(connection, transaction, patientUserId, appointmentId, cancellationToken);
        EnsureMinimumNotice(appointment.StartsAt);
        var newSlotProviderId = await FindSlotProviderAsync(connection, transaction, newSlotId, cancellationToken)
            ?? throw new DomainException("slot_no_longer_available", "The selected slot is no longer available.", StatusCodes.Status409Conflict);
        if (newSlotProviderId != appointment.ProviderId)
            throw new DomainException("provider_change_not_supported", "Rescheduling must use a slot with the same provider.");
        await LockProviderAsync(connection, transaction, appointment.ProviderId, cancellationToken);

        var newStartAt = await ClaimOpenSlotAsync(connection, transaction, newSlotId, appointment.ProviderId, cancellationToken);
        if (newStartAt is null) throw new DomainException("slot_no_longer_available", "The selected slot is no longer available.", StatusCodes.Status409Conflict);

        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        var newVersion = appointment.ScheduleVersion + 1;
        await ExecuteAsync(connection, transaction, "UPDATE slots SET status = 'open' WHERE id = $1 AND provider_id = $2 AND status = 'booked'", cancellationToken, appointment.SlotId, appointment.ProviderId);
        await ExecuteAsync(connection, transaction, "UPDATE appointments SET slot_id = $1, start_at = $2, schedule_version = $3, updated_at = $4 WHERE id = $5", cancellationToken, newSlotId, newStartAt.Value, newVersion, now, appointment.Id);
        await SupersedePendingRemindersAsync(connection, transaction, appointment.Id, appointment.ScheduleVersion, now, cancellationToken);
        if (ReminderSchedule.IsDueBeforeStart(newStartAt.Value, now, reminderOptions.Value))
            await ReminderSchedule.InsertAsync(connection, transaction, appointment.Id, newVersion, newStartAt.Value, appointment.ReminderRecipientEmail, reminderOptions.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(appointment.Id, newSlotId, newStartAt.Value, newVersion, "confirmed");
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for appointment changes.");
        var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private void EnsureMinimumNotice(DateTimeOffset startsAt)
    {
        if (startsAt - clock.GetCurrentInstant().ToDateTimeOffset() < TimeSpan.FromHours(24))
            throw new DomainException("minimum_notice_required", "Appointments can only be changed at least 24 hours before they start.");
    }

    private static async Task<AppointmentRow> FindOwnedConfirmedAppointmentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid patientUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id, slot_id, provider_id, start_at, schedule_version, reminder_recipient_email FROM appointments WHERE id = $1 AND patient_user_id = $2 AND status = 'confirmed' FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(appointmentId); command.Parameters.AddWithValue(patientUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new DomainException("appointment_not_found", "The appointment was not found.", StatusCodes.Status404NotFound);
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), ReadUtc(reader.GetValue(3)), reader.GetInt32(4), reader.IsDBNull(5) ? throw new InvalidOperationException("The appointment has no reminder recipient.") : reader.GetString(5));
    }

    private static async Task<Guid?> FindSlotProviderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid slotId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT provider_id FROM slots WHERE id = $1", connection, transaction);
        command.Parameters.AddWithValue(slotId);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid providerId ? providerId : null;
    }

    private static Task LockProviderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid providerId, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))", cancellationToken, providerId);

    private static async Task<DateTimeOffset?> ClaimOpenSlotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid slotId, Guid providerId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE slots SET status = 'booked' WHERE id = $1 AND provider_id = $2 AND status = 'open' RETURNING start_at", connection, transaction);
        command.Parameters.AddWithValue(slotId); command.Parameters.AddWithValue(providerId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : ReadUtc(result);
    }

    private static Task SupersedePendingRemindersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid appointmentId, int version, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "UPDATE email_outbox SET status = 'superseded', updated_at = $1 WHERE appointment_id = $2 AND schedule_version = $3 AND kind = 'reminder' AND status IN ('pending', 'claimed')", cancellationToken, now, appointmentId, version);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset ReadUtc(object value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException("Expected a timestamp with time zone.")
    };

    private sealed record AppointmentRow(Guid Id, Guid SlotId, Guid ProviderId, DateTimeOffset StartsAt, int ScheduleVersion, string ReminderRecipientEmail);
}
