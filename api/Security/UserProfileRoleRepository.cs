using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Security;

public interface IUserProfileRoleRepository
{
    Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed class UserProfileRoleRepository(IOptions<DatabaseOptions> databaseOptions) : IUserProfileRoleRepository
{
    public async Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand("SELECT role FROM user_profiles WHERE user_id = $1");
        command.Parameters.AddWithValue(userId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string role && Enum.TryParse<AppRole>(role, ignoreCase: true, out var parsed) ? parsed : null;
    }
}
