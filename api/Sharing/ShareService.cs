using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Sharing;

public sealed record ShareRequest(string ResourceType, Guid ResourceId, string RecipientEmail);
public sealed record MintedShare(string Link, DateTimeOffset ExpiresAt);

public interface IShareService
{
    Task<MintedShare?> MintAsync(Guid accountId, ShareRequest request, CancellationToken cancellationToken);
}

public interface IShareTokenGenerator
{
    string Create();
}

public sealed class ShareTokenGenerator : IShareTokenGenerator
{
    public string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class ShareService(
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<ShareOptions> shareOptions,
    IClock clock,
    IShareTokenGenerator tokenGenerator,
    ILogger<ShareService> logger) : IShareService
{
    private static readonly Duration Lifetime = Duration.FromHours(48);

    public async Task<MintedShare?> MintAsync(Guid accountId, ShareRequest request, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for shares.");

        var startedAt = clock.GetCurrentInstant();
        var resourceType = request.ResourceType.Trim().ToLowerInvariant();
        if (resourceType is not ("image" or "report")) return null;

        var publicUrl = shareOptions.Value.PublicUrl.TrimEnd('/');
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("APP_URL must be an absolute URL.");

        var token = tokenGenerator.Create();
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = startedAt;
        var expiresAt = now + Lifetime;
        var shareId = Guid.NewGuid();
        var link = $"{publicUrl}/s/{token}";

        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await IsOwnedResourceAsync(connection, transaction, accountId, resourceType, request.ResourceId, cancellationToken))
        {
            await InsertAuditAsync(connection, transaction, accountId, "share_mint_denied", request.ResourceId, "denied", now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var payload = JsonSerializer.Serialize(new
        {
            to = request.RecipientEmail,
            subject = "A medical image or report has been shared with you",
            html = $"<p>A medical image or report has been shared with you.</p><p><a href=\"{link}\">Open shared item</a></p><p>This link expires in 48 hours.</p>",
            text = $"A medical image or report has been shared with you. Open the link: {link} This link expires in 48 hours.",
        });

        await InsertShareAsync(connection, transaction, shareId, tokenHash, resourceType, request.ResourceId, request.RecipientEmail, expiresAt, cancellationToken);
        await InsertOutboxAsync(connection, transaction, shareId, payload, now, cancellationToken);
        await InsertAuditAsync(connection, transaction, accountId, "share_minted", shareId, "allowed", now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Share minted {ShareId} {ResourceType} {ElapsedMilliseconds}ms", shareId, resourceType, (clock.GetCurrentInstant() - startedAt).TotalMilliseconds);
        return new MintedShare(link, expiresAt.ToDateTimeOffset());
    }

    private static async Task<bool> IsOwnedResourceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId, string resourceType, Guid resourceId, CancellationToken cancellationToken)
    {
        var sql = resourceType == "image"
            ? """
                SELECT EXISTS (
                    SELECT 1 FROM images
                    INNER JOIN studies ON studies.id = images.study_id
                    INNER JOIN patient_records ON patient_records.id = studies.patient_record_id
                    WHERE images.id = @resource_id AND patient_records.claimed_by = @account_id
                      AND studies.visit_status = 'completed' AND studies.performed_at IS NOT NULL AND studies.performed_at <= CURRENT_TIMESTAMP)
                """
            : """
                SELECT EXISTS (
                    SELECT 1 FROM reports
                    INNER JOIN patient_records ON patient_records.id = reports.patient_record_id
                    WHERE reports.id = @resource_id AND patient_records.claimed_by = @account_id
                      AND reports.status = 'signed' AND reports.signed_at IS NOT NULL)
                """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("resource_id", resourceId);
        command.Parameters.AddWithValue("account_id", accountId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task InsertShareAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid shareId, string tokenHash, string resourceType, Guid resourceId, string recipientEmail, Instant expiresAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO share_links (id, token_hash, resource_type, resource_id, recipient_email, expires_at) VALUES (@id, @token_hash, @resource_type, @resource_id, @recipient_email, @expires_at)", connection, transaction);
        command.Parameters.AddWithValue("id", shareId); command.Parameters.AddWithValue("token_hash", tokenHash); command.Parameters.AddWithValue("resource_type", resourceType); command.Parameters.AddWithValue("resource_id", resourceId); command.Parameters.AddWithValue("recipient_email", recipientEmail); command.Parameters.AddWithValue("expires_at", expiresAt.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid shareId, string payload, Instant now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO email_outbox (id, kind, payload, due_at, status, idempotency_key) VALUES (@id, 'share', CAST(@payload AS jsonb), @due_at, 'pending', @idempotency_key)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("payload", payload); command.Parameters.AddWithValue("due_at", now.ToDateTimeOffset()); command.Parameters.AddWithValue("idempotency_key", $"share/{shareId}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId, string action, Guid target, string result, Instant now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result, occurred_at) VALUES (@id, @actor, 'patient', @action, 'share_link', @target, @result, @now)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("actor", accountId.ToString()); command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("target", target.ToString()); command.Parameters.AddWithValue("result", result); command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
