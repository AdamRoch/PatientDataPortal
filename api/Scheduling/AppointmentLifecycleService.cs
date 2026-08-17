using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Errors;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Scheduling;

public sealed record AppointmentStatusRequest(string Status);
public sealed record AppointmentStatusConfirmation(Guid Id, string Status);

public interface IAppointmentLifecycleService
{
    Task<AppointmentStatusConfirmation> TransitionAsync(Guid actorUserId, AppRole actorRole, Guid appointmentId, string status, CancellationToken cancellationToken);
}

public sealed class AppointmentLifecycleService(IOptions<DatabaseOptions> databaseOptions, IClock clock) : IAppointmentLifecycleService
{
    public async Task<AppointmentStatusConfirmation> TransitionAsync(Guid actorUserId, AppRole actorRole, Guid appointmentId, string status, CancellationToken cancellationToken)
    {
        if (actorRole is not (AppRole.Provider or AppRole.Admin))
            throw new DomainException("appointment_status_forbidden", "Only providers and administrators can change appointment status.", StatusCodes.Status403Forbidden);
        if (status is not ("completed" or "cancelled" or "no-show"))
            throw new DomainException("invalid_appointment_status", "The requested appointment status is not supported.");

        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for appointment status changes.");
        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var appointment = await FindAppointmentAsync(connection, transaction, appointmentId, cancellationToken)
            ?? throw new DomainException("appointment_not_found", "The appointment was not found.", StatusCodes.Status404NotFound);
        if (actorRole == AppRole.Provider && !await IsOwningProviderAsync(connection, transaction, actorUserId, appointment.ProviderId, cancellationToken))
            throw new DomainException("appointment_not_found", "The appointment was not found.", StatusCodes.Status404NotFound);
        if (!IsAllowed(appointment.Status, status))
            throw new DomainException("invalid_appointment_transition", $"An appointment cannot move from {appointment.Status} to {status}.", StatusCodes.Status409Conflict);
        if (status == "no-show" && appointment.StartsAt > clock.GetCurrentInstant().ToDateTimeOffset())
            throw new DomainException("appointment_not_started", "An appointment can only be marked no-show after it starts.", StatusCodes.Status409Conflict);

        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        await ExecuteAsync(connection, transaction, "UPDATE appointments SET status = $1, updated_at = $2 WHERE id = $3", cancellationToken, status, now, appointment.Id);
        await ExecuteAsync(connection, transaction, "INSERT INTO appointment_events (id, appointment_id, from_status, to_status, actor_user_id, actor_role, occurred_at) VALUES ($1, $2, $3, $4, $5, $6, $7)", cancellationToken, Guid.NewGuid(), appointment.Id, appointment.Status, status, actorUserId, RoleText(actorRole), now);
        await ExecuteAsync(connection, transaction, "INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result, occurred_at) VALUES ($1, $2, $3, $4, 'appointment', $5, 'allowed', $6)", cancellationToken, Guid.NewGuid(), actorUserId.ToString(), RoleText(actorRole), $"appointment_{status}", appointment.Id.ToString(), now);
        if (status == "cancelled")
        {
            await ExecuteAsync(connection, transaction, "UPDATE slots SET status = 'open' WHERE id = $1 AND provider_id = $2 AND status = 'booked'", cancellationToken, appointment.SlotId, appointment.ProviderId);
            await ExecuteAsync(connection, transaction, "UPDATE email_outbox SET status = 'superseded', updated_at = $1 WHERE appointment_id = $2 AND schedule_version = $3 AND kind = 'reminder' AND status = 'pending'", cancellationToken, now, appointment.Id, appointment.ScheduleVersion);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(appointment.Id, status);
    }

    private static bool IsAllowed(string from, string to) => (from, to) switch
    {
        ("requested", "cancelled") => true,
        ("confirmed", "completed" or "cancelled" or "no-show") => true,
        _ => false
    };

    private static async Task<AppointmentRow?> FindAppointmentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid appointmentId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id, slot_id, provider_id, start_at, schedule_version, status FROM appointments WHERE id = $1 FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(appointmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), ReadUtc(reader.GetValue(3)), reader.GetInt32(4), reader.GetString(5)) : null;
    }

    private static async Task<bool> IsOwningProviderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid userId, Guid providerId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM providers WHERE id = $1 AND user_id = $2)", connection, transaction);
        command.Parameters.AddWithValue(providerId); command.Parameters.AddWithValue(userId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string RoleText(AppRole role) => role.ToString().ToLowerInvariant();
    private static DateTimeOffset ReadUtc(object value) => value is DateTimeOffset offset ? offset : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
    private sealed record AppointmentRow(Guid Id, Guid SlotId, Guid ProviderId, DateTimeOffset StartsAt, int ScheduleVersion, string Status);
}
