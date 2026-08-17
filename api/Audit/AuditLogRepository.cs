using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Audit;

public sealed record AuditLogItem(string? ActorReference, string ActorRole, string Action, string TargetType, string TargetReference, string Result, DateTimeOffset OccurredAt);
public sealed record AuditLogFilters(string? Actor, string? Action, DateOnly? Date);

public interface IAuditLogRepository
{
    Task<IReadOnlyList<AuditLogItem>> ListForAdminAsync(AuditLogFilters filters, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditLogItem>> ListForProviderAsync(Guid providerUserId, AuditLogFilters filters, CancellationToken cancellationToken);
}

public sealed class AuditLogRepository(IOptions<DatabaseOptions> databaseOptions) : IAuditLogRepository
{
    public Task<IReadOnlyList<AuditLogItem>> ListForAdminAsync(AuditLogFilters filters, CancellationToken cancellationToken) =>
        ListAsync("", filters, null, cancellationToken);

    public Task<IReadOnlyList<AuditLogItem>> ListForProviderAsync(Guid providerUserId, AuditLogFilters filters, CancellationToken cancellationToken) =>
        ListAsync("""
            WITH provider_patients AS (
                SELECT DISTINCT patient_record.id
                FROM providers
                JOIN appointments ON appointments.provider_id = providers.id
                JOIN patient_records AS patient_record ON patient_record.claimed_by = appointments.patient_user_id
                WHERE providers.user_id = @provider_user_id
            ),
            provider_audit_targets AS (
                SELECT appointments.id::text AS target_reference, 'appointment' AS target_type
                FROM appointments JOIN providers ON providers.id = appointments.provider_id
                WHERE providers.user_id = @provider_user_id
                UNION
                SELECT patient_records.id::text, 'patient_record' FROM patient_records JOIN provider_patients ON provider_patients.id = patient_records.id
                UNION
                SELECT studies.id::text, 'study' FROM studies JOIN provider_patients ON provider_patients.id = studies.patient_record_id
                UNION
                SELECT images.id::text, 'image' FROM images JOIN studies ON studies.id = images.study_id JOIN provider_patients ON provider_patients.id = studies.patient_record_id
                UNION
                SELECT cine_clips.id::text, 'cine_clip' FROM cine_clips JOIN studies ON studies.id = cine_clips.study_id JOIN provider_patients ON provider_patients.id = studies.patient_record_id
                UNION
                SELECT reports.id::text, 'report' FROM reports JOIN provider_patients ON provider_patients.id = reports.patient_record_id
                UNION
                SELECT share_links.id::text, 'share_link'
                FROM share_links
                LEFT JOIN images ON share_links.resource_type = 'image' AND images.id = share_links.resource_id
                LEFT JOIN studies ON studies.id = images.study_id
                LEFT JOIN reports ON share_links.resource_type = 'report' AND reports.id = share_links.resource_id
                JOIN provider_patients ON provider_patients.id = CASE WHEN share_links.resource_type = 'image' THEN studies.patient_record_id ELSE reports.patient_record_id END
                UNION
                SELECT audit_subject_links.audit_reference::text, 'deletion_request'
                FROM audit_subject_links JOIN provider_patients ON provider_patients.id = audit_subject_links.patient_record_id
            )
            """, filters, providerUserId, cancellationToken);

    private async Task<IReadOnlyList<AuditLogItem>> ListAsync(string scope, AuditLogFilters filters, Guid? providerUserId, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for audit log viewing.");

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand($"""
            {scope}
            SELECT actor_reference, actor_role, action, target_type, target_reference, result, occurred_at
            FROM audit_log
            WHERE (@actor IS NULL OR actor_reference = @actor)
              AND (@action IS NULL OR action = @action)
              AND (@from IS NULL OR occurred_at >= @from)
              AND (@until IS NULL OR occurred_at < @until)
              {(providerUserId is null ? "" : "AND EXISTS (SELECT 1 FROM provider_audit_targets WHERE provider_audit_targets.target_type = audit_log.target_type AND provider_audit_targets.target_reference = audit_log.target_reference)")}
            ORDER BY occurred_at DESC, id DESC
            LIMIT 100;
            """);
        command.Parameters.AddWithValue("actor", (object?)filters.Actor ?? DBNull.Value);
        command.Parameters.AddWithValue("action", (object?)filters.Action ?? DBNull.Value);
        command.Parameters.AddWithValue("from", filters.Date is { } date ? date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : DBNull.Value);
        command.Parameters.AddWithValue("until", filters.Date is { } untilDate ? untilDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : DBNull.Value);
        if (providerUserId is { } id) command.Parameters.AddWithValue("provider_user_id", id);

        var results = new List<AuditLogItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new AuditLogItem(
                reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)));
        return results;
    }
}
