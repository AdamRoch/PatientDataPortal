using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Profiles;

public sealed class PatientProfileRepository(IOptions<DatabaseOptions> databaseOptions) : IPatientProfileRepository
{
    public async Task<PatientProfile?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("SELECT display_name, tz FROM user_profiles WHERE user_id = $1");
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PatientProfile(reader.GetString(0), reader.GetString(1))
            : null;
    }

    public async Task<PatientProfile?> UpdateAsync(Guid userId, string displayName, string timeZone, CancellationToken cancellationToken)
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand(
            "UPDATE user_profiles SET display_name = $2, tz = $3 WHERE user_id = $1 RETURNING display_name, tz");
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(displayName);
        command.Parameters.AddWithValue(timeZone);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PatientProfile(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private NpgsqlDataSource CreateDataSource()
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Database is not configured.");
        return NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
    }
}
