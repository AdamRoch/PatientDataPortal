using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Sharing;

public sealed record ManagedShare(
    Guid Id,
    string ResourceType,
    Guid ResourceId,
    string RecipientEmail,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    string Status);

public interface IShareManagementService
{
    Task<IReadOnlyList<ManagedShare>> ListAsync(Guid accountId, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid accountId, Guid shareId, CancellationToken cancellationToken);
}

public sealed class ShareManagementService(
    IOptions<DatabaseOptions> databaseOptions,
    IClock clock,
    ILogger<ShareManagementService> logger) : IShareManagementService
{
    public async Task<IReadOnlyList<ManagedShare>> ListAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT share_links.id, share_links.resource_type, share_links.resource_id, share_links.recipient_email,
                   share_links.expires_at, share_links.created_at, share_links.revoked_at,
                   CASE
                     WHEN share_links.revoked_at IS NOT NULL THEN 'revoked'
                     WHEN share_links.expires_at <= @now THEN 'expired'
                     ELSE 'active'
                   END AS status
            FROM share_links
            LEFT JOIN images ON share_links.resource_type = 'image' AND images.id = share_links.resource_id
            LEFT JOIN studies ON images.study_id = studies.id
            LEFT JOIN reports ON share_links.resource_type = 'report' AND reports.id = share_links.resource_id
            INNER JOIN patient_records ON patient_records.id = CASE
                WHEN share_links.resource_type = 'image' THEN studies.patient_record_id
                ELSE reports.patient_record_id
            END
            WHERE patient_records.claimed_by = @account_id
            ORDER BY share_links.created_at DESC
            """, connection);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("now", clock.GetCurrentInstant().ToDateTimeOffset());

        var shares = new List<ManagedShare>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            shares.Add(new ManagedShare(
                reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7)));
        }
        return shares;
    }

    public async Task<bool> RevokeAsync(Guid accountId, Guid shareId, CancellationToken cancellationToken)
    {
        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var revoke = new NpgsqlCommand("""
            UPDATE share_links
            SET revoked_at = @now
            WHERE id = @share_id
              AND revoked_at IS NULL
              AND EXISTS (
                SELECT 1
                FROM share_links owned_share
                LEFT JOIN images ON owned_share.resource_type = 'image' AND images.id = owned_share.resource_id
                LEFT JOIN studies ON images.study_id = studies.id
                LEFT JOIN reports ON owned_share.resource_type = 'report' AND reports.id = owned_share.resource_id
                INNER JOIN patient_records ON patient_records.id = CASE
                    WHEN owned_share.resource_type = 'image' THEN studies.patient_record_id
                    ELSE reports.patient_record_id
                END
                WHERE owned_share.id = share_links.id AND patient_records.claimed_by = @account_id)
            RETURNING id
            """, connection, transaction);
        revoke.Parameters.AddWithValue("now", now);
        revoke.Parameters.AddWithValue("share_id", shareId);
        revoke.Parameters.AddWithValue("account_id", accountId);
        var revoked = await revoke.ExecuteScalarAsync(cancellationToken) is Guid;
        if (!revoked)
        {
            await InsertAuditAsync(connection, transaction, accountId, "share_revoke_denied", shareId, "denied", now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await InsertAuditAsync(connection, transaction, accountId, "share_revoked", shareId, "allowed", now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Share revoked {ShareId}", shareId);
        return true;
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId, string action, Guid shareId, string result, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result, occurred_at)
            VALUES (@id, @actor, 'patient', @action, 'share_link', @target, @result, @now)
            """, connection, transaction);
        audit.Parameters.AddWithValue("id", Guid.NewGuid());
        audit.Parameters.AddWithValue("actor", accountId.ToString());
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("target", shareId.ToString());
        audit.Parameters.AddWithValue("result", result);
        audit.Parameters.AddWithValue("now", now);
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for share management.");
        var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
