using Npgsql;
using PatientDataPortal.Api.Configuration;
using System.Security.Cryptography;

namespace PatientDataPortal.Api.Migrations;

public static class MigrationRunner
{
    public static async Task<string> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var bootstrapConnectionString = RequiredEnvironment("MIGRATION_DATABASE_URL", "DATABASE_URL");
        var bootstrapBuilder = new NpgsqlConnectionStringBuilder(DatabaseConnectionString.Normalize(bootstrapConnectionString));
        var applicationRole = Environment.GetEnvironmentVariable("APP_DB_ROLE") ?? "patient_data_portal_app_v1";
        var applicationPassword = Environment.GetEnvironmentVariable("APP_DB_PASSWORD") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var applicationConnectionString = ApplicationConnectionString(bootstrapBuilder, applicationRole, applicationPassword);

        await using var connection = new NpgsqlConnection(bootstrapBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureApplicationRoleAsync(connection, applicationRole, applicationPassword, cancellationToken);
        await using (var command = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
            await command.ExecuteNonQueryAsync(cancellationToken);

        foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Migrations"), "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var version = Path.GetFileName(path);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = @version)", connection, transaction);
            check.Parameters.AddWithValue("version", version);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken))!) { await transaction.RollbackAsync(cancellationToken); continue; }
            var sql = (await File.ReadAllTextAsync(path, cancellationToken)).Replace("{{APP_DB_ROLE}}", QuoteIdentifier(applicationRole), StringComparison.Ordinal);
            await using (var migration = new NpgsqlCommand(sql, connection, transaction)) await migration.ExecuteNonQueryAsync(cancellationToken);
            await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@version)", connection, transaction)) { record.Parameters.AddWithValue("version", version); await record.ExecuteNonQueryAsync(cancellationToken); }
            await transaction.CommitAsync(cancellationToken);
        }

        return applicationConnectionString;
    }

    private static async Task EnsureApplicationRoleAsync(NpgsqlConnection connection, string role, string password, CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role)", connection);
        exists.Parameters.AddWithValue("role", role);
        if ((bool)(await exists.ExecuteScalarAsync(cancellationToken))!) return;
        var statement = $"CREATE ROLE {QuoteIdentifier(role)} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD {QuoteLiteral(password)}";
        await using var command = new NpgsqlCommand(statement, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ApplicationConnectionString(NpgsqlConnectionStringBuilder bootstrap, string role, string password)
    {
        var username = bootstrap.Username ?? string.Empty;
        var application = new NpgsqlConnectionStringBuilder(bootstrap.ConnectionString)
        {
            Username = (bootstrap.Host ?? string.Empty).Contains("pooler", StringComparison.OrdinalIgnoreCase) && username.Contains('.')
                ? role + username.Substring(username.IndexOf('.'))
                : role,
            Password = password,
        };
        return application.ConnectionString;
    }

    private static string RequiredEnvironment(params string[] names)
    {
        foreach (var name in names)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) return value;
        throw new InvalidOperationException($"One of {string.Join(", ", names)} must be set.");
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
