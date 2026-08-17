using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Cine;

public sealed class CineRepository(IOptions<DatabaseOptions> databaseOptions) : ICineRepository
{
    public async Task<CineClipAccess?> GetOwnedAsync(Guid clipId, Guid accountId, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for cine clips.");

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand("""
            SELECT cine_clips.id, cine_clips.manifest
            FROM cine_clips
            INNER JOIN studies ON studies.id = cine_clips.study_id
            INNER JOIN patient_records ON patient_records.id = studies.patient_record_id
            WHERE cine_clips.id = @clip_id
              AND patient_records.claimed_by = @account_id
              AND studies.visit_status = 'completed'
              AND studies.performed_at IS NOT NULL
              AND studies.performed_at <= CURRENT_TIMESTAMP
            """);
        command.Parameters.AddWithValue("clip_id", clipId);
        command.Parameters.AddWithValue("account_id", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var manifest = JsonDocument.Parse(reader.GetString(1)).RootElement.Clone();
        var paths = manifest.GetProperty("frames").EnumerateArray()
            .Select(frame => frame.GetProperty("path").GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        return new CineClipAccess(reader.GetGuid(0), manifest, paths);
    }
}
