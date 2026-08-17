using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Studies;
using Xunit;

namespace PatientDataPortal.Api.Tests;

[Trait("Category", "Integration")]
public sealed class StudyRepositoryTests
{
    [Fact]
    public async Task ListsOnlyTheClaimedPatientsCompletedPastStudies()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var account = Guid.NewGuid();
        var otherAccount = Guid.NewGuid();
        var ownPatient = Guid.NewGuid();
        var otherPatient = Guid.NewGuid();
        var included = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync();
        await InsertPatientAsync(connection, ownPatient, account);
        await InsertPatientAsync(connection, otherPatient, otherAccount);
        await InsertStudyAsync(connection, included, ownPatient, "completed", DateTimeOffset.UtcNow.AddDays(-1));
        await InsertStudyAsync(connection, Guid.NewGuid(), ownPatient, "scheduled", null);
        await InsertStudyAsync(connection, Guid.NewGuid(), ownPatient, "cancelled", null);
        await InsertStudyAsync(connection, Guid.NewGuid(), ownPatient, "completed", DateTimeOffset.UtcNow.AddDays(1));
        await InsertStudyAsync(connection, Guid.NewGuid(), otherPatient, "completed", DateTimeOffset.UtcNow.AddDays(-1));

        var repository = new StudyRepository(Options.Create(new DatabaseOptions { ConnectionString = connectionString }));
        var studies = await repository.ListCompletedForPatientAsync(account, default);

        var study = Assert.Single(studies);
        Assert.Equal(included, study.Id);
    }

    private static async Task InsertPatientAsync(NpgsqlConnection connection, Guid patientId, Guid accountId)
    {
        await using var command = new NpgsqlCommand("INSERT INTO patient_records (id, patient_ref, dob, full_name, claimed_by) VALUES (@id, @reference, '1980-01-02', 'Test Patient', @account)", connection);
        command.Parameters.AddWithValue("id", patientId);
        command.Parameters.AddWithValue("reference", "PTDP-study-" + patientId.ToString("N"));
        command.Parameters.AddWithValue("account", accountId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertStudyAsync(NpgsqlConnection connection, Guid studyId, Guid patientId, string status, DateTimeOffset? performedAt)
    {
        await using var command = new NpgsqlCommand("INSERT INTO studies (id, patient_record_id, performed_at, visit_status, description) VALUES (@id, @patient, @performed, @status, 'Test study')", connection);
        command.Parameters.AddWithValue("id", studyId);
        command.Parameters.AddWithValue("patient", patientId);
        command.Parameters.AddWithValue("performed", (object?)performedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
    }
}
