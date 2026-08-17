using Npgsql;
using NodaTime;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Migrations;

public static class MigrationVerifier
{
    public static async Task VerifyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var patient = Guid.NewGuid();
        var providerUser = Guid.NewGuid();
        var record = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var service = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var appointment = Guid.NewGuid();
        var study = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());

        await ExecuteAsync(connection, transaction, "INSERT INTO user_profiles (user_id, role, display_name, tz) VALUES (@patient, 'patient', 'Migration verifier', 'UTC'), (@provider_user, 'provider', 'Migration verifier', 'UTC')", cancellationToken, ("patient", patient), ("provider_user", providerUser));
        await ExecuteAsync(connection, transaction, "INSERT INTO patient_records (id, patient_ref, dob, full_name) VALUES (@record, 'migration-verifier-ref', DATE '2000-01-01', 'Migration verifier')", cancellationToken, ("record", record));
        await ExecuteAsync(connection, transaction, "INSERT INTO studies (id, patient_record_id, performed_at, visit_status, description) VALUES (@study, @record, @performed_at, 'completed', 'Synthetic verification study')", cancellationToken, ("study", study), ("record", record), ("performed_at", now));
        await ExecuteAsync(connection, transaction, "INSERT INTO reports (id, patient_record_id, study_id, status, signed_at, signed_by, storage_path) VALUES (@id, @record, @study, 'signed', @signed_at, @signed_by, @path), (@preliminary_id, @record, @study, 'preliminary', NULL, NULL, @preliminary_path)", cancellationToken, ("id", Guid.NewGuid()), ("record", record), ("study", study), ("signed_at", now), ("signed_by", providerUser), ("path", "reports/migration-verifier-signed.pdf"), ("preliminary_id", Guid.NewGuid()), ("preliminary_path", "reports/migration-verifier-preliminary.pdf"));
        await ExecuteAsync(connection, transaction, "INSERT INTO providers (id, user_id, tz, slot_length_min) VALUES (@provider, @provider_user, 'UTC', 30)", cancellationToken, ("provider", provider), ("provider_user", providerUser));
        await ExecuteAsync(connection, transaction, "INSERT INTO services (id, provider_id, name) VALUES (@service, @provider, 'Verification')", cancellationToken, ("service", service), ("provider", provider));
        await ExecuteAsync(connection, transaction, "INSERT INTO slots (id, provider_id, start_at, end_at, status) VALUES (@slot, @provider, @start_at, @end_at, 'booked')", cancellationToken, ("slot", slot), ("provider", provider), ("start_at", now), ("end_at", now.AddMinutes(30)));
        await ExecuteAsync(connection, transaction, "INSERT INTO appointments (id, slot_id, patient_user_id, provider_id, service_id, start_at, status, idempotency_key) VALUES (@appointment, @slot, @patient, @provider, @service, @start_at, 'confirmed', 'migration-verifier-1')", cancellationToken, ("appointment", appointment), ("slot", slot), ("patient", patient), ("provider", provider), ("service", service), ("start_at", now));
        await ExecuteAsync(connection, transaction, "INSERT INTO email_outbox (id, appointment_id, schedule_version, interval, kind, payload, due_at, status, idempotency_key) VALUES (@id, @appointment, 1, '24h', 'reminder', '{}'::jsonb, @due_at, 'pending', 'migration-verifier-reminder-1')", cancellationToken, ("id", Guid.NewGuid()), ("appointment", appointment), ("due_at", now));
        await ExecuteAsync(connection, transaction, "INSERT INTO share_links (id, token_hash, resource_type, resource_id, recipient_email, expires_at) VALUES (@id, 'migration-verifier-token', 'report', @record, 'recipient@example.test', @expires_at)", cancellationToken, ("id", Guid.NewGuid()), ("record", record), ("expires_at", now.AddHours(1)));

        await ExpectUniqueViolationAsync(connection, transaction, "INSERT INTO appointments (id, slot_id, patient_user_id, provider_id, service_id, start_at, status, idempotency_key) VALUES (@id, @slot, @patient, @provider, @service, @start_at, 'requested', 'migration-verifier-2')", cancellationToken, ("id", Guid.NewGuid()), ("slot", slot), ("patient", patient), ("provider", provider), ("service", service), ("start_at", now));
        await ExpectUniqueViolationAsync(connection, transaction, "INSERT INTO email_outbox (id, appointment_id, schedule_version, interval, kind, payload, due_at, status, idempotency_key) VALUES (@id, @appointment, 1, '24h', 'reminder', '{}'::jsonb, @due_at, 'pending', 'migration-verifier-reminder-2')", cancellationToken, ("id", Guid.NewGuid()), ("appointment", appointment), ("due_at", now));
        await ExpectUniqueViolationAsync(connection, transaction, "INSERT INTO share_links (id, token_hash, resource_type, resource_id, recipient_email, expires_at) VALUES (@id, 'migration-verifier-token', 'report', @record, 'recipient@example.test', @expires_at)", cancellationToken, ("id", Guid.NewGuid()), ("record", record), ("expires_at", now.AddHours(1)));
        await ExpectCheckViolationAsync(connection, transaction, "INSERT INTO reports (id, patient_record_id, study_id, status, signed_at, signed_by, storage_path) VALUES (@id, @record, @study, 'preliminary', NULL, NULL, 'https://storage.example.test/report.pdf')", cancellationToken, ("id", Guid.NewGuid()), ("record", record), ("study", study));
        await ExpectPermissionDeniedAsync(connection, transaction, "UPDATE audit_log SET action = 'tampered'", cancellationToken);
        await ExpectPermissionDeniedAsync(connection, transaction, "DELETE FROM audit_log", cancellationToken);

        await transaction.RollbackAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExpectUniqueViolationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var savepoint = new NpgsqlCommand("SAVEPOINT negative_case", connection, transaction);
        await savepoint.ExecuteNonQueryAsync(cancellationToken);
        try { await ExecuteAsync(connection, transaction, sql, cancellationToken, parameters); throw new InvalidOperationException("Expected a unique-constraint violation."); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT negative_case", connection, transaction);
            await rollback.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ExpectPermissionDeniedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var savepoint = new NpgsqlCommand("SAVEPOINT permission_case", connection, transaction);
        await savepoint.ExecuteNonQueryAsync(cancellationToken);
        try { await ExecuteAsync(connection, transaction, sql, cancellationToken); throw new InvalidOperationException("Expected audit_log permission denial."); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT permission_case", connection, transaction);
            await rollback.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ExpectCheckViolationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var savepoint = new NpgsqlCommand("SAVEPOINT check_case", connection, transaction);
        await savepoint.ExecuteNonQueryAsync(cancellationToken);
        try { await ExecuteAsync(connection, transaction, sql, cancellationToken, parameters); throw new InvalidOperationException("Expected a check-constraint violation."); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT check_case", connection, transaction);
            await rollback.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
