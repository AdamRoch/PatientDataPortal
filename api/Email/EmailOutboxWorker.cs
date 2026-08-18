using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Email;

public interface IEmailOutboxProcessor
{
    Task<EmailOutboxRunResult> ProcessAsync(CancellationToken cancellationToken);
}

public sealed class EmailOutboxWorker(
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<OutboxOptions> outboxOptions,
    IEmailSender emailSender,
    IClock clock,
    ILogger<EmailOutboxWorker> logger) : IEmailOutboxProcessor
{
    public async Task<EmailOutboxRunResult> ProcessAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseOptions.Value.ConnectionString))
            throw new InvalidOperationException("DATABASE_URL is required for the email outbox worker.");

        var result = new EmailOutboxRunResult();
        for (var processed = 0; processed < outboxOptions.Value.BatchSize; processed++)
        {
            var now = clock.GetCurrentInstant();
            var claimed = await ClaimDueMessageAsync(now, cancellationToken);
            if (claimed is null) break;

            result.Claimed++;
            await DeliverAsync(claimed, now, result, cancellationToken);
        }

        return result;
    }

    private async Task<ClaimedOutboxMessage?> ClaimDueMessageAsync(Instant now, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(databaseOptions.Value.ConnectionString));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH next_message AS (
                SELECT id
                FROM email_outbox
                WHERE due_at <= @now
                  AND (status IN ('pending', 'failed')
                       OR (status = 'claimed' AND lease_expires_at <= @now))
                ORDER BY due_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE email_outbox AS outbox
            SET status = 'claimed',
                attempts = attempts + 1,
                claimed_at = @now,
                lease_expires_at = @lease_expires_at,
                updated_at = @now
            FROM next_message
            WHERE outbox.id = next_message.id
            RETURNING outbox.id, outbox.kind, outbox.appointment_id, outbox.schedule_version,
                      outbox.payload::text, outbox.idempotency_key, outbox.attempts;
            """, connection);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        command.Parameters.AddWithValue("lease_expires_at", (now + Duration.FromMinutes(outboxOptions.Value.LeaseMinutes)).ToDateTimeOffset());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ClaimedOutboxMessage(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6));
    }

    private async Task DeliverAsync(ClaimedOutboxMessage message, Instant now, EmailOutboxRunResult result, CancellationToken cancellationToken)
    {
        if (message.Kind == "reminder" && await SupersedeIfStaleReminderAsync(message, now, cancellationToken))
        {
            result.Superseded++;
            logger.LogInformation("Outbox reminder superseded before delivery {OutboxId} {Attempt}", message.Id, message.Attempts);
            return;
        }

        EmailMessage email;
        try
        {
            var payload = JsonSerializer.Deserialize<EmailOutboxPayload>(message.Payload, JsonOptions) ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(payload.To) || string.IsNullOrWhiteSpace(payload.Subject) || string.IsNullOrWhiteSpace(payload.Html))
                throw new JsonException();
            email = new EmailMessage(payload.To, payload.Subject, payload.Html, message.IdempotencyKey, payload.Text);
        }
        catch (JsonException)
        {
            await MarkFailedAsync(message, now, "invalid_outbox_payload", false, cancellationToken);
            result.Failed++;
            logger.LogWarning("Outbox email attempt failed {OutboxId} {Kind} {Attempt} {ErrorCode} {Retryable}", message.Id, message.Kind, message.Attempts, "invalid_outbox_payload", false);
            return;
        }

        var sendResult = await emailSender.SendAsync(email, cancellationToken);
        if (sendResult.Succeeded)
        {
            await MarkSentAsync(message, sendResult.ProviderMessageId!, now, cancellationToken);
            result.Sent++;
            logger.LogInformation("Outbox email accepted {OutboxId} {Kind} {Attempt} {ProviderMessageId}", message.Id, message.Kind, message.Attempts, sendResult.ProviderMessageId);
            return;
        }

        var failure = sendResult.Failure!;
        await MarkFailedAsync(message, now, failure.Code, failure.IsRetryable, cancellationToken);
        result.Failed++;
        logger.LogWarning("Outbox email attempt failed {OutboxId} {Kind} {Attempt} {ErrorCode} {Retryable}", message.Id, message.Kind, message.Attempts, failure.Code, failure.IsRetryable);
    }

    private async Task<bool> SupersedeIfStaleReminderAsync(ClaimedOutboxMessage message, Instant now, CancellationToken cancellationToken)
    {
        if (message.AppointmentId is null || message.ScheduleVersion is null) return true;

        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(databaseOptions.Value.ConnectionString));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE email_outbox AS outbox
            SET status = 'superseded', lease_expires_at = NULL, updated_at = @now
            WHERE outbox.id = @id
              AND outbox.status = 'claimed'
              AND outbox.kind = 'reminder'
              AND NOT EXISTS (
                  SELECT 1
                  FROM appointments
                  WHERE id = @appointment_id
                    AND status = 'confirmed'
                    AND schedule_version = @schedule_version);
            """, connection);
        command.Parameters.AddWithValue("id", message.Id);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        command.Parameters.AddWithValue("appointment_id", message.AppointmentId.Value);
        command.Parameters.AddWithValue("schedule_version", message.ScheduleVersion.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task MarkSentAsync(ClaimedOutboxMessage message, string providerMessageId, Instant now, CancellationToken cancellationToken)
    {
        await ExecuteUpdateAsync("""
            UPDATE email_outbox
            SET status = 'sent', provider_message_id = @provider_message_id, sent_at = @now,
                lease_expires_at = NULL, updated_at = @now, payload = '{}'::jsonb
            WHERE id = @id AND status = 'claimed';
            """, message.Id, now, cancellationToken, providerMessageId: providerMessageId);
    }

    private Task MarkFailedAsync(ClaimedOutboxMessage message, Instant now, string errorCode, bool retryable, CancellationToken cancellationToken)
    {
        var retry = retryable && message.Attempts < outboxOptions.Value.MaximumAttempts;
        var dueAt = retry ? now + RetryDelay(message.Attempts) : Instant.MaxValue;
        return ExecuteUpdateAsync("""
            UPDATE email_outbox
            SET status = @status, due_at = @due_at, lease_expires_at = NULL, updated_at = @now
            WHERE id = @id AND status = 'claimed';
            """, message.Id, now, cancellationToken, status: retry ? "pending" : "failed", dueAt: dueAt);
    }

    private async Task ExecuteUpdateAsync(string sql, Guid id, Instant now, CancellationToken cancellationToken, string? providerMessageId = null, string? status = null, Instant? dueAt = null)
    {
        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(databaseOptions.Value.ConnectionString));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        if (providerMessageId is not null) command.Parameters.AddWithValue("provider_message_id", providerMessageId);
        if (status is not null) command.Parameters.AddWithValue("status", status);
        if (dueAt is not null) command.Parameters.AddWithValue("due_at", dueAt.Value.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Duration RetryDelay(int attempts) => Duration.FromMinutes(Math.Min(60, 1 << Math.Min(attempts - 1, 6)));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record ClaimedOutboxMessage(Guid Id, string Kind, Guid? AppointmentId, int? ScheduleVersion, string Payload, string IdempotencyKey, int Attempts);
    private sealed record EmailOutboxPayload(string To, string Subject, string Html, string? Text);
}

public sealed class EmailOutboxRunResult
{
    public int Claimed { get; internal set; }
    public int Sent { get; internal set; }
    public int Failed { get; internal set; }
    public int Superseded { get; internal set; }
}
