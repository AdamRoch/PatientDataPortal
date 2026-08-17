using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using System.Text.Json.Serialization;

namespace PatientDataPortal.Api.Deletion;

public sealed record DeletionRequest(Guid Id, string Status, DateTimeOffset RequestedAt, [property: JsonIgnore] Guid AuditReference);
public sealed record AdminDeletionRequest(Guid Id, string Status, DateTimeOffset RequestedAt, string? PatientReference);

public interface IDeletionRequestService
{
    Task<DeletionRequest?> RequestAsync(Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminDeletionRequest>> ListPendingAsync(CancellationToken cancellationToken);
}

public sealed class DeletionRequestService(IOptions<DatabaseOptions> databaseOptions) : IDeletionRequestService
{
    public async Task<DeletionRequest?> RequestAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH patient AS (SELECT id FROM patient_records WHERE claimed_by = @account_id FOR UPDATE),
            inserted AS (
              INSERT INTO deletion_requests (id, patient_record_id, requested_by, audit_reference, status)
              SELECT @id, patient.id, @account_id, @audit_reference, 'pending' FROM patient
              ON CONFLICT (patient_record_id) WHERE status = 'pending' DO NOTHING
              RETURNING id, status, requested_at, patient_record_id, audit_reference)
            INSERT INTO audit_subject_links (audit_reference, patient_record_id)
            SELECT audit_reference, patient_record_id FROM inserted
            ON CONFLICT (audit_reference) DO NOTHING
            RETURNING (SELECT id FROM inserted), (SELECT status FROM inserted), (SELECT requested_at FROM inserted), (SELECT audit_reference FROM inserted)
            """, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("audit_reference", Guid.NewGuid());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.RollbackAsync(cancellationToken);
            await using var existing = new NpgsqlCommand("""
                SELECT id, status, requested_at, audit_reference FROM deletion_requests
                WHERE requested_by = @account_id AND status = 'pending' ORDER BY requested_at DESC LIMIT 1
                """, connection);
            existing.Parameters.AddWithValue("account_id", accountId);
            await using var existingReader = await existing.ExecuteReaderAsync(cancellationToken);
            return await existingReader.ReadAsync(cancellationToken)
                ? new DeletionRequest(existingReader.GetGuid(0), existingReader.GetString(1), existingReader.GetFieldValue<DateTimeOffset>(2), existingReader.GetGuid(3))
                : null;
        }
        var result = new DeletionRequest(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetGuid(3));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<AdminDeletionRequest>> ListPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT deletion_requests.id, deletion_requests.status, deletion_requests.requested_at, patient_records.patient_ref
            FROM deletion_requests LEFT JOIN patient_records ON patient_records.id = deletion_requests.patient_record_id
            WHERE deletion_requests.status = 'pending' ORDER BY deletion_requests.requested_at
            """, connection);
        var requests = new List<AdminDeletionRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            requests.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return requests;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var value = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("DATABASE_URL is required for deletion requests.");
        var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(value));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
