using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Sharing;
using Xunit;

namespace PatientDataPortal.Api.Tests;

[Trait("Category", "Integration")]
[Collection("share-service")]
public sealed class ShareServiceTests
{
    [Fact]
    public async Task OwnedImage_MintsHashedToken_AndAtomicallyQueuesAuditAndEmail()
    {
        await using var fixture = await ShareFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var image = await fixture.SeedImageAsync(fixture.AccountId);

        var minted = await fixture.Service().MintAsync(fixture.AccountId, new ShareRequest("image", image, "recipient@example.test"), default);

        Assert.NotNull(minted);
        Assert.StartsWith("https://portal.example.test/s/", minted!.Link, StringComparison.Ordinal);
        Assert.Equal((fixture.Clock.GetCurrentInstant() + Duration.FromHours(48)).ToDateTimeOffset(), minted.ExpiresAt);
        var token = minted.Link.Split('/').Last();
        var share = await fixture.ShareAsync(image);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), share.TokenHash);
        Assert.Equal(minted.ExpiresAt, share.ExpiresAt);
        Assert.DoesNotContain(token, share.TokenHash, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.OutboxCountAsync());
        Assert.Equal(1, await fixture.AuditCountAsync());
        var payload = await fixture.OutboxPayloadAsync();
        Assert.Contains(token, payload, StringComparison.Ordinal);
        Assert.Contains("A medical image or report has been shared with you", payload, StringComparison.Ordinal);
        Assert.Contains("expires in 48 hours", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Test Patient", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Test study", payload, StringComparison.Ordinal);
        Assert.Equal($"share/{share.Id}", await fixture.OutboxIdempotencyKeyAsync());
    }

    [Fact]
    public async Task ForeignImageAndPreliminaryReportAreNotShareable()
    {
        await using var fixture = await ShareFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var foreignImage = await fixture.SeedImageAsync(Guid.NewGuid());
        var preliminaryReport = await fixture.SeedReportAsync(fixture.AccountId, signed: false);

        var foreign = await fixture.Service().MintAsync(fixture.AccountId, new ShareRequest("image", foreignImage, "recipient@example.test"), default);
        var preliminary = await fixture.Service().MintAsync(fixture.AccountId, new ShareRequest("report", preliminaryReport, "recipient@example.test"), default);

        Assert.Null(foreign);
        Assert.Null(preliminary);
        Assert.Equal(0, await fixture.OutboxCountAsync());
        Assert.Equal(2, await fixture.AuditCountAsync("share_mint_denied"));
    }

    [Fact]
    public async Task DuplicateRequestsCreateIndependentLinks()
    {
        await using var fixture = await ShareFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var report = await fixture.SeedReportAsync(fixture.AccountId, signed: true);

        var first = await fixture.Service().MintAsync(fixture.AccountId, new ShareRequest("report", report, "recipient@example.test"), default);
        var second = await fixture.Service().MintAsync(fixture.AccountId, new ShareRequest("report", report, "recipient@example.test"), default);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Link, second!.Link);
        Assert.Equal(2, await fixture.ShareCountAsync());
        Assert.Equal(2, await fixture.OutboxCountAsync());
        Assert.Equal(2, await fixture.AuditCountAsync());
    }

    [Fact]
    public async Task FailedShareInsertRollsBackWithoutAnOrphanOutboxOrAuditRow()
    {
        await using var fixture = await ShareFixture.CreateAsync(new FixedTokenGenerator("a-fixed-test-token"));
        if (!fixture.HasDatabase) return;
        var image = await fixture.SeedImageAsync(fixture.AccountId);
        var request = new ShareRequest("image", image, "recipient@example.test");
        await fixture.Service().MintAsync(fixture.AccountId, request, default);

        await Assert.ThrowsAsync<PostgresException>(() => fixture.Service().MintAsync(fixture.AccountId, request, default));

        Assert.Equal(1, await fixture.ShareCountAsync());
        Assert.Equal(1, await fixture.OutboxCountAsync());
        Assert.Equal(1, await fixture.AuditCountAsync());
    }

    [Fact]
    public async Task ListShowsOnlyThePatientsOwnLinksWithComputedStatus()
    {
        await using var fixture = await ShareFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var image = await fixture.SeedImageAsync(fixture.AccountId);
        var report = await fixture.SeedReportAsync(fixture.AccountId, signed: true);
        var foreignImage = await fixture.SeedImageAsync(Guid.NewGuid());
        var active = await fixture.InsertShareAsync(image, "active", "active@example.test", fixture.Clock.GetCurrentInstant().Plus(Duration.FromHours(1)));
        var expired = await fixture.InsertShareAsync(report, "expired", "expired@example.test", fixture.Clock.GetCurrentInstant().Minus(Duration.FromHours(1)));
        var revoked = await fixture.InsertShareAsync(image, "revoked", "revoked@example.test", fixture.Clock.GetCurrentInstant().Plus(Duration.FromHours(1)));
        await fixture.InsertShareAsync(foreignImage, "foreign", "foreign@example.test", fixture.Clock.GetCurrentInstant().Plus(Duration.FromHours(1)));
        await fixture.Management().RevokeAsync(fixture.AccountId, revoked, default);

        var shares = await fixture.Management().ListAsync(fixture.AccountId, default);

        Assert.Equal(3, shares.Count);
        Assert.Contains(shares, share => share.Id == active && share.ResourceType == "image" && share.RecipientEmail == "active@example.test" && share.Status == "active");
        Assert.Contains(shares, share => share.Id == expired && share.ResourceType == "report" && share.RecipientEmail == "expired@example.test" && share.Status == "expired");
        Assert.Contains(shares, share => share.Id == revoked && share.Status == "revoked" && share.RevokedAt is not null);
        Assert.DoesNotContain(shares, share => share.RecipientEmail == "foreign@example.test");
    }

    [Fact]
    public async Task RevokingAnOwnedLinkMakesThePublicLookupUnavailableAndWritesAudit()
    {
        await using var fixture = await ShareFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var image = await fixture.SeedImageAsync(fixture.AccountId);
        var share = await fixture.InsertShareAsync(image, "live-public-token", "recipient@example.test", fixture.Clock.GetCurrentInstant().Plus(Duration.FromDays(90)));
        var publicShares = new PublicShareService(Options.Create(new DatabaseOptions { ConnectionString = fixture.ConnectionString }), fixture.Clock);

        Assert.NotNull(await publicShares.FindActiveAsync("live-public-token", default));
        Assert.True(await fixture.Management().RevokeAsync(fixture.AccountId, share, default));

        Assert.Null(await publicShares.FindActiveAsync("live-public-token", default));
        Assert.Equal(1, await fixture.AuditCountAsync("share_revoked"));
    }

    [Fact]
    public async Task APatientCannotRevokeAnotherPatientsLink()
    {
        await using var fixture = await ShareFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var foreignImage = await fixture.SeedImageAsync(Guid.NewGuid());
        var foreignShare = await fixture.InsertShareAsync(foreignImage, "foreign-token", "foreign@example.test", fixture.Clock.GetCurrentInstant().Plus(Duration.FromDays(1)));

        Assert.False(await fixture.Management().RevokeAsync(fixture.AccountId, foreignShare, default));
        Assert.Equal(0, await fixture.AuditCountAsync("share_revoked"));
        Assert.Equal(1, await fixture.AuditCountAsync("share_revoke_denied"));
        Assert.Null(await fixture.RevokedAtAsync(foreignShare));
    }

    private sealed class ShareFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly IShareTokenGenerator _tokens;
        public Guid AccountId { get; } = Guid.NewGuid();
        public bool HasDatabase => !string.IsNullOrWhiteSpace(_connectionString);
        public string ConnectionString => _connectionString;
        public FakeClock Clock { get; } = new(Instant.FromUtc(2026, 8, 16, 12, 0));

        private ShareFixture(string connectionString, IShareTokenGenerator tokens) { _connectionString = connectionString; _tokens = tokens; }
        public static Task<ShareFixture> CreateAsync(IShareTokenGenerator? tokens = null) => Task.FromResult(new ShareFixture(Environment.GetEnvironmentVariable("DATABASE_URL") ?? string.Empty, tokens ?? new ShareTokenGenerator()));
        public ShareService Service() => new(Options.Create(new DatabaseOptions { ConnectionString = _connectionString }), Options.Create(new ShareOptions { PublicUrl = "https://portal.example.test" }), Clock, _tokens, NullLogger<ShareService>.Instance);
        public ShareManagementService Management() => new(Options.Create(new DatabaseOptions { ConnectionString = _connectionString }), Clock, NullLogger<ShareManagementService>.Instance);

        public async Task<Guid> SeedImageAsync(Guid owner)
        {
            var record = await SeedRecordAsync(owner); var study = await SeedStudyAsync(record); var id = Guid.NewGuid();
            await ExecuteAsync("INSERT INTO images (id, study_id, storage_path, thumbnail_path) VALUES (@id, @study, @path, @thumbnail)", ("id", id), ("study", study), ("path", $"images/{id}.png"), ("thumbnail", $"images/{id}-thumb.png"));
            return id;
        }

        public async Task<Guid> SeedReportAsync(Guid owner, bool signed)
        {
            var record = await SeedRecordAsync(owner); var study = await SeedStudyAsync(record); var id = Guid.NewGuid();
            await ExecuteAsync("INSERT INTO reports (id, patient_record_id, study_id, status, signed_at, signed_by, storage_path) VALUES (@id, @record, @study, @status, @signed_at, @signed_by, @path)", ("id", id), ("record", record), ("study", study), ("status", signed ? "signed" : "preliminary"), ("signed_at", signed ? Clock.GetCurrentInstant().ToDateTimeOffset() : DBNull.Value), ("signed_by", signed ? Guid.NewGuid() : DBNull.Value), ("path", $"reports/{id}.pdf"));
            return id;
        }

        public async Task<(Guid Id, string TokenHash, DateTimeOffset ExpiresAt)> ShareAsync(Guid resourceId) => await OneAsync<(Guid, string, DateTimeOffset)>("SELECT id, token_hash, expires_at FROM share_links WHERE resource_id = @resource", ("resource", resourceId), reader => (reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        public Task<int> ShareCountAsync() => ScalarAsync<int>("SELECT count(*)::int FROM share_links");
        public Task<int> OutboxCountAsync() => ScalarAsync<int>("SELECT count(*)::int FROM email_outbox WHERE kind = 'share'");
        public Task<int> AuditCountAsync() => ScalarAsync<int>("SELECT count(*)::int FROM audit_log WHERE action = 'share_minted'");
        public Task<int> AuditCountAsync(string action) => ScalarAsync<int>("SELECT count(*)::int FROM audit_log WHERE action = @action", ("action", action));
        public Task<string> OutboxPayloadAsync() => ScalarAsync<string>("SELECT payload::text FROM email_outbox WHERE kind = 'share' LIMIT 1");
        public Task<string> OutboxIdempotencyKeyAsync() => ScalarAsync<string>("SELECT idempotency_key FROM email_outbox WHERE kind = 'share' LIMIT 1");

        private async Task<Guid> SeedRecordAsync(Guid owner)
        {
            var id = Guid.NewGuid();
            await ExecuteAsync("INSERT INTO patient_records (id, patient_ref, dob, full_name, claimed_by) VALUES (@id, @ref, DATE '2000-01-01', 'Test Patient', @owner)", ("id", id), ("ref", $"test-{id}"), ("owner", owner));
            return id;
        }

        private async Task<Guid> SeedStudyAsync(Guid record)
        {
            var id = Guid.NewGuid();
            await ExecuteAsync("INSERT INTO studies (id, patient_record_id, performed_at, visit_status, description) VALUES (@id, @record, @performed, 'completed', 'Test study')", ("id", id), ("record", record), ("performed", Clock.GetCurrentInstant().ToDateTimeOffset()));
            return id;
        }

        public async Task<Guid> InsertShareAsync(Guid resourceId, string token, string recipientEmail, Instant expiresAt)
        {
            var id = Guid.NewGuid();
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            var resourceType = await ScalarAsync<string>("SELECT CASE WHEN EXISTS (SELECT 1 FROM images WHERE id = @resource) THEN 'image' ELSE 'report' END", ("resource", resourceId));
            await ExecuteAsync("INSERT INTO share_links (id, token_hash, resource_type, resource_id, recipient_email, expires_at) VALUES (@id, @hash, @type, @resource, @recipient, @expires)", ("id", id), ("hash", tokenHash), ("type", resourceType), ("resource", resourceId), ("recipient", recipientEmail), ("expires", expiresAt.ToDateTimeOffset()));
            return id;
        }
        public async Task<DateTimeOffset?> RevokedAtAsync(Guid shareId)
        {
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand("SELECT revoked_at FROM share_links WHERE id = @id", connection); command.Parameters.AddWithValue("id", shareId);
            var value = await command.ExecuteScalarAsync(); return value is DBNull ? null : (DateTimeOffset)value!;
        }
        private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand(sql, connection); foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return (T)(await command.ExecuteScalarAsync())!;
        }
        private async Task<T> OneAsync<T>(string sql, (string Name, object Value) parameter, Func<NpgsqlDataReader, T> read)
        {
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue(parameter.Name, parameter.Value); await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); return read(reader);
        }
        private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand(sql, connection); foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value); await command.ExecuteNonQueryAsync();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTokenGenerator(string token) : IShareTokenGenerator { public string Create() => token; }
}

[CollectionDefinition("share-service", DisableParallelization = true)]
public sealed class ShareServiceCollection;
