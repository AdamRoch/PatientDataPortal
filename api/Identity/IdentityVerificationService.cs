using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Identity;

public sealed record IdentityVerificationRequest(string? PatientRef, string? Dob);
public sealed record IdentityVerificationResult(bool Succeeded, Duration ThrottleDelay);

public interface IIdentityVerificationService
{
    Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken);
    Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken);
    Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken);
}

public sealed class IdentityVerificationService(
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<IdentityVerificationOptions> identityOptions,
    IClock clock) : IIdentityVerificationService
{
    public const string GenericErrorCode = "identity_verification_failed";
    private static readonly Duration LockoutDuration = Duration.FromMinutes(15);
    private static readonly Duration ThrottleDuration = Duration.FromMilliseconds(250);
    private const int MaximumFailures = 5;
    private const int ThrottleThreshold = 10;

    public async Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for identity verification.");
        var now = clock.GetCurrentInstant();
        var patientReference = request.PatientRef?.Trim() ?? string.Empty;
        var hasDob = LocalDatePattern.Iso.Parse(request.Dob ?? string.Empty).TryGetValue(default, out var suppliedDob);
        var networkHmac = Hmac(networkIdentity);
        var referenceHmac = Hmac(patientReference);

        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@account_id::text))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("account_id", accountId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var accountFailures = await CountAsync(connection, transaction, "account_id = @account_id AND result IN ('denied', 'locked')", accountId, null, now - LockoutDuration, cancellationToken);
        var networkAttempts = await CountAsync(connection, transaction, "network_hmac = @network_hmac", accountId, networkHmac, now - LockoutDuration, cancellationToken);
        var referenceAttempts = await CountAsync(connection, transaction, "patient_ref_hmac = @reference_hmac", accountId, referenceHmac, now - LockoutDuration, cancellationToken);
        var throttled = networkAttempts >= ThrottleThreshold || referenceAttempts >= ThrottleThreshold;

        var patient = await FindPatientAsync(connection, transaction, patientReference, cancellationToken);
        // Always compare fixed-size digests, including when no record exists. Do not replace this
        // dummy path with an early return: it is the unknown-reference timing equalizer.
        var expectedReference = patient?.PatientReference ?? "__dummy_patient_reference__";
        var expectedDob = patient?.Dob ?? new LocalDate(1900, 1, 1);
        var referenceMatches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(patientReference)), SHA256.HashData(Encoding.UTF8.GetBytes(expectedReference)));
        var dobMatches = hasDob && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(suppliedDob.ToString("yyyy-MM-dd", null))),
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedDob.ToString("yyyy-MM-dd", null))));
        var canClaim = accountFailures < MaximumFailures && emailVerified && patient is { ClaimedBy: null } && referenceMatches && dobMatches;

        if (canClaim)
        {
            var claimed = await ClaimAsync(connection, transaction, patient!.Value.Id, accountId, now, cancellationToken);
            if (claimed)
            {
                await RecordAsync(connection, transaction, accountId, networkHmac, referenceHmac, "allowed", now, "allowed", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new IdentityVerificationResult(true, throttled ? ThrottleDuration : Duration.Zero);
            }
        }

        var result = accountFailures + 1 >= MaximumFailures ? "locked" : "denied";
        await RecordAsync(connection, transaction, accountId, networkHmac, referenceHmac, result, now, "denied", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IdentityVerificationResult(false, throttled ? ThrottleDuration : Duration.Zero);
    }

    public async Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseOptions.Value.ConnectionString)) return false;
        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(databaseOptions.Value.ConnectionString));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM patient_records WHERE claimed_by = @account_id)", connection);
        command.Parameters.AddWithValue("account_id", accountId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databaseOptions.Value.ConnectionString)) throw new InvalidOperationException("DATABASE_URL is required for claim recovery.");
        var now = clock.GetCurrentInstant();
        await using var connection = new NpgsqlConnection(DatabaseConnectionString.Normalize(databaseOptions.Value.ConnectionString));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = new NpgsqlCommand("UPDATE patient_records SET claimed_by = NULL, claimed_at = NULL WHERE id = @id AND claimed_by IS NOT NULL RETURNING id", connection, transaction);
        update.Parameters.AddWithValue("id", patientRecordId);
        if (await update.ExecuteScalarAsync(cancellationToken) is null) { await transaction.RollbackAsync(cancellationToken); return; }
        foreach (var eventType in new[] { "unlink", "recovery" })
        {
            await using var command = new NpgsqlCommand("INSERT INTO patient_claim_events (id, patient_record_id, actor_user_id, event_type, occurred_at, reason_code) VALUES (@id, @record_id, @actor_id, @event_type, @now, @reason_code)", connection, transaction);
            command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("record_id", patientRecordId); command.Parameters.AddWithValue("actor_id", adminId); command.Parameters.AddWithValue("event_type", eventType); command.Parameters.AddWithValue("now", now.ToDateTimeOffset()); command.Parameters.AddWithValue("reason_code", (object?)reasonCode ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteAuditAsync(connection, transaction, adminId.ToString(), "admin", "patient_claim_unlink", "patient_record", patientRecordId.ToString(), "allowed", now, cancellationToken);
        await WriteAuditAsync(connection, transaction, adminId.ToString(), "admin", "patient_claim_recovery", "patient_record", patientRecordId.ToString(), "allowed", now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<int> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string predicate, Guid accountId, string? hmac, Instant since, CancellationToken cancellationToken)
    {
        var sql = $"SELECT count(*)::int FROM verification_attempts WHERE {predicate} AND attempted_at >= @since";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId);
        if (predicate.Contains("network_hmac", StringComparison.Ordinal)) command.Parameters.AddWithValue("network_hmac", hmac!);
        if (predicate.Contains("patient_ref_hmac", StringComparison.Ordinal)) command.Parameters.AddWithValue("reference_hmac", hmac!);
        command.Parameters.AddWithValue("since", since.ToDateTimeOffset());
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(Guid Id, string PatientReference, LocalDate Dob, Guid? ClaimedBy)?> FindPatientAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string patientReference, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id, patient_ref, dob, claimed_by FROM patient_records WHERE patient_ref = @patient_ref", connection, transaction);
        command.Parameters.AddWithValue("patient_ref", patientReference);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetGuid(0), reader.GetString(1), LocalDate.FromDateTime(reader.GetDateTime(2)), reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    private static async Task<bool> ClaimAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid recordId, Guid accountId, Instant now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE patient_records SET claimed_by = @account_id, claimed_at = @now WHERE id = @id AND claimed_by IS NULL", connection, transaction);
        command.Parameters.AddWithValue("account_id", accountId); command.Parameters.AddWithValue("now", now.ToDateTimeOffset()); command.Parameters.AddWithValue("id", recordId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await using var claimEvent = new NpgsqlCommand("INSERT INTO patient_claim_events (id, patient_record_id, actor_user_id, event_type, occurred_at) VALUES (@id, @record_id, @actor_id, 'claim', @now)", connection, transaction);
        claimEvent.Parameters.AddWithValue("id", Guid.NewGuid()); claimEvent.Parameters.AddWithValue("record_id", recordId); claimEvent.Parameters.AddWithValue("actor_id", accountId); claimEvent.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        await claimEvent.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(connection, transaction, accountId.ToString(), "patient", "patient_claim", "patient_record", recordId.ToString(), "allowed", now, cancellationToken);
        return true;
    }

    private static async Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId, string networkHmac, string referenceHmac, string result, Instant now, string auditResult, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO verification_attempts (id, account_id, network_hmac, patient_ref_hmac, result, attempted_at) VALUES (@id, @account_id, @network_hmac, @reference_hmac, @result, @now)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("account_id", accountId); command.Parameters.AddWithValue("network_hmac", networkHmac); command.Parameters.AddWithValue("reference_hmac", referenceHmac); command.Parameters.AddWithValue("result", result); command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await WriteAuditAsync(connection, transaction, accountId.ToString(), "patient", "identity_verification", "identity_claim", referenceHmac, auditResult, now, cancellationToken);
    }

    private static async Task WriteAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string actor, string role, string action, string targetType, string target, string result, Instant now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO audit_log (id, actor_reference, actor_role, action, target_type, target_reference, result, occurred_at) VALUES (@id, @actor, @role, @action, @target_type, @target, @result, @now)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("role", role); command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("target_type", targetType); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("result", result); command.Parameters.AddWithValue("now", now.ToDateTimeOffset());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string Hmac(string value)
    {
        var key = identityOptions.Value.HmacKey;
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("IDENTITY_HMAC_KEY is required for identity verification.");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value)));
    }
}
