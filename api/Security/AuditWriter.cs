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
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
    Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    Task WriteAllowedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) => WriteAsync(auditEvent, cancellationToken);
}

public sealed class AuditWriter(IOptions<DatabaseOptions> databaseOptions, ILogger<AuditWriter> logger) : IAuditWriter
{
    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) => WriteAsync(auditEvent, auditEvent.Result, suppressFailure: true, cancellationToken);

    public async Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        => await WriteAsync(auditEvent, "denied", suppressFailure: true, cancellationToken);

    public async Task WriteAllowedAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        => await WriteAsync(auditEvent, auditEvent.Result, suppressFailure: false, cancellationToken);

    private async Task WriteAsync(AuditEvent auditEvent, string result, bool suppressFailure, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning("Skipped access audit event because the application database is not configured.");
            if (!suppressFailure) throw new InvalidOperationException("Database access is required to audit this resource view.");
            return;
        }

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
            await using var command = dataSource.CreateCommand("""
                INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """);
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue((object?)auditEvent.ActorReference ?? DBNull.Value);
            command.Parameters.AddWithValue(auditEvent.ActorRole);
            command.Parameters.AddWithValue(auditEvent.Action);
            command.Parameters.AddWithValue(auditEvent.TargetType);
            command.Parameters.AddWithValue(auditEvent.TargetReference);
            command.Parameters.AddWithValue(result);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or ArgumentException)
        {
            logger.LogError(exception, "Failed to persist access audit event.");
            if (!suppressFailure) throw;
        }
    }
}
