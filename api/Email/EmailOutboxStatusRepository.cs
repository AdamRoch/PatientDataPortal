using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Email;

public sealed class EmailOutboxStatusRepository(IOptions<DatabaseOptions> databaseOptions) : IEmailOutboxStatusRepository
{
    public async Task<IReadOnlyList<EmailOutboxStatusItem>> ListAsync(CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for email outbox status.");

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand("""
            SELECT kind, status, attempts, due_at, sent_at, provider_message_id
            FROM email_outbox
            ORDER BY due_at DESC, id DESC
            LIMIT 100;
            """);
        var results = new List<EmailOutboxStatusItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new EmailOutboxStatusItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return results;
    }
}
