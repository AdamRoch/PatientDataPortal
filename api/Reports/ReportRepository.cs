using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Reports;

public sealed class ReportRepository(IOptions<DatabaseOptions> databaseOptions) : IReportRepository
{
    public async Task<IReadOnlyList<SignedReportListItem>> ListSignedForPatientAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var connectionString = RequireConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand("""
            SELECT reports.id, reports.signed_at, studies.description
            FROM reports
            INNER JOIN patient_records ON patient_records.id = reports.patient_record_id
            INNER JOIN studies ON studies.id = reports.study_id
            WHERE patient_records.claimed_by = @account_id
              AND reports.status = 'signed'
              AND reports.signed_at IS NOT NULL
            ORDER BY reports.signed_at DESC, reports.id DESC
            """);
        command.Parameters.AddWithValue("account_id", accountId);

        var reports = new List<SignedReportListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            reports.Add(new SignedReportListItem(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2)));
        return reports;
    }

    public async Task<SignedReportStorageItem?> FindSignedForPatientAsync(Guid reportId, Guid accountId, CancellationToken cancellationToken)
    {
        var connectionString = RequireConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand("""
            SELECT reports.id, reports.storage_path
            FROM reports
            INNER JOIN patient_records ON patient_records.id = reports.patient_record_id
            WHERE reports.id = @report_id
              AND patient_records.claimed_by = @account_id
              AND reports.status = 'signed'
            """);
        command.Parameters.AddWithValue("report_id", reportId);
        command.Parameters.AddWithValue("account_id", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SignedReportStorageItem(reader.GetGuid(0), reader.GetString(1))
            : null;
    }

    private string RequireConnectionString() => string.IsNullOrWhiteSpace(databaseOptions.Value.ConnectionString)
        ? throw new InvalidOperationException("DATABASE_URL is required for reports.")
        : databaseOptions.Value.ConnectionString;
}
