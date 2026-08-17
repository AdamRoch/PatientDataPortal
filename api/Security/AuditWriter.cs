using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Security;

public sealed record AuditEvent(
    string? ActorReference,
    string ActorRole,
    string Action,
    string TargetType,
    string TargetReference,
    string Result);

public interface IAuditWriter
{
    Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

public sealed class AuditWriter(IOptions<DatabaseOptions> databaseOptions, ILogger<AuditWriter> logger) : IAuditWriter
{
    public async Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("Skipped denied-access audit event because the application database is not configured.");
            return;
        }

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
            await using var command = dataSource.CreateCommand("""
                INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result)
                VALUES ($1, $2, $3, $4, $5, $6, 'denied')
                """);
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue((object?)auditEvent.ActorReference ?? DBNull.Value);
            command.Parameters.AddWithValue(auditEvent.ActorRole);
            command.Parameters.AddWithValue(auditEvent.Action);
            command.Parameters.AddWithValue(auditEvent.TargetType);
            command.Parameters.AddWithValue(auditEvent.TargetReference);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or ArgumentException)
        {
            logger.LogError(exception, "Failed to persist denied-access audit event.");
        }
    }
}
