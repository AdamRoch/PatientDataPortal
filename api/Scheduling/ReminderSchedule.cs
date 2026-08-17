using System.Text.Json;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Scheduling;

internal static class ReminderSchedule
{
    public static bool IsDueBeforeStart(DateTimeOffset startsAt, DateTimeOffset now, ReminderOptions options) =>
        startsAt - now > TimeSpan.FromMinutes(options.LeadMinutes);

    public static Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid appointmentId,
        int scheduleVersion,
        DateTimeOffset startsAt,
        string recipientEmail,
        ReminderOptions options,
        CancellationToken cancellationToken)
    {
        var portalUrl = options.PortalUrl.TrimEnd('/');
        if (!Uri.TryCreate(portalUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("APP_URL must be an absolute URL.");

        var interval = Interval(options.LeadMinutes);
        var payload = JsonSerializer.Serialize(new
        {
            to = recipientEmail,
            subject = "You have a portal notification",
            html = $"<p>You have a portal notification.</p><p><a href=\"{portalUrl}\">Open the patient portal</a></p>",
            text = $"You have a portal notification. Open the patient portal: {portalUrl}"
        });
        return ExecuteAsync(connection, transaction, "INSERT INTO email_outbox (id, appointment_id, schedule_version, interval, kind, payload, due_at, status, idempotency_key) VALUES ($1, $2, $3, $4, 'reminder', CAST($5 AS jsonb), $6 - ($7 * INTERVAL '1 minute'), 'pending', $8)", cancellationToken,
            Guid.NewGuid(), appointmentId, scheduleVersion, interval, payload, startsAt, options.LeadMinutes, $"appointment/{appointmentId}/{scheduleVersion}/{interval}");
    }

    public static string Interval(int leadMinutes) => leadMinutes == ReminderOptions.DefaultLeadMinutes ? "24h" : $"{leadMinutes}m";

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
