using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Studies;

public sealed class StudyRepository(IOptions<DatabaseOptions> databaseOptions) : IStudyRepository
{
    public async Task<IReadOnlyList<StudyListItem>> ListCompletedForPatientAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for studies.");

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand("""
            SELECT studies.id, studies.performed_at, studies.description,
                   COALESCE(array_agg(images.id) FILTER (WHERE images.id IS NOT NULL), ARRAY[]::uuid[])
            FROM studies
            INNER JOIN patient_records ON patient_records.id = studies.patient_record_id
            LEFT JOIN images ON images.study_id = studies.id
            WHERE patient_records.claimed_by = @account_id
              AND studies.visit_status = 'completed'
              AND studies.performed_at IS NOT NULL
              AND studies.performed_at <= CURRENT_TIMESTAMP
            GROUP BY studies.id, studies.performed_at, studies.description
            ORDER BY studies.performed_at DESC, studies.id DESC
            """);
        command.Parameters.AddWithValue("account_id", accountId);

        var results = new List<StudyListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new StudyListItem(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetFieldValue<Guid[]>(3)));
        return results;
    }
}
