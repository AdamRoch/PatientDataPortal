using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Email;
using Xunit;

namespace PatientDataPortal.Api.Tests;

[Trait("Category", "Integration")]
[Collection("email-outbox")]
public sealed class EmailOutboxWorkerTests : IAsyncLifetime
{
    private const string ShareUrl = "https://portal.example.test/share/a-secret-token";
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);

    public Task InitializeAsync() => DatabaseGate.WaitAsync();
    public Task DisposeAsync()
    {
        DatabaseGate.Release();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OverlappingRuns_ClaimAndDeliverEachRowOnce()
    {
        await using var fixture = await OutboxFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        await fixture.InsertAsync("concurrent", fixture.Clock.GetCurrentInstant());
        var sender = new BlockingSender();
        var firstRun = fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None);
        await sender.FirstSendStarted.Task;

        var second = await fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None);
        sender.ReleaseFirstSend();
        var first = await firstRun;

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Claimed);
        Assert.Single(sender.Keys);
        Assert.Equal("outbox/concurrent", sender.Keys.Single());
        Assert.Equal("sent", await fixture.StatusAsync("concurrent"));
    }

    [Fact]
    public async Task CrashAfterProviderAcceptance_IsRecoveredWithTheSameIdempotencyKey()
    {
        await using var fixture = await OutboxFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        await fixture.InsertAsync("crash", fixture.Clock.GetCurrentInstant());
        var sender = new AcceptThenCrashSender();

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None));
        Assert.Equal("claimed", await fixture.StatusAsync("crash"));

        fixture.Clock.Advance(Duration.FromMinutes(6));
        var recovered = await fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None);

        Assert.Equal(1, recovered.Sent);
        Assert.Equal(new[] { "outbox/crash", "outbox/crash" }, sender.Keys);
        Assert.Single(sender.ProviderCommands);
        Assert.Equal("sent", await fixture.StatusAsync("crash"));
    }

    [Fact]
    public async Task LateTick_CatchesUpEveryDueRow()
    {
        await using var fixture = await OutboxFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        await fixture.InsertAsync("late-1", fixture.Clock.GetCurrentInstant() - Duration.FromHours(1));
        await fixture.InsertAsync("late-2", fixture.Clock.GetCurrentInstant() - Duration.FromHours(2));
        await fixture.InsertAsync("late-3", fixture.Clock.GetCurrentInstant() - Duration.FromHours(3));
        var sender = new RecordingSender();

        var run = await fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None);

        Assert.Equal(3, run.Sent);
        Assert.Equal(3, sender.Keys.Count);
        Assert.Equal(3, await fixture.CountByStatusAsync("sent"));
    }

    [Fact]
    public async Task SuccessfulShareDelivery_ScrubsThePlaintextLink()
    {
        await using var fixture = await OutboxFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        await fixture.InsertAsync("scrub", fixture.Clock.GetCurrentInstant(), ShareUrl);

        await fixture.CreateWorker(new RecordingSender()).ProcessAsync(CancellationToken.None);

        var payload = await fixture.PayloadAsync("scrub");
        Assert.Equal("{}", payload);
        Assert.DoesNotContain("a-secret-token", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryableFailure_UsesBackoffAndStopsAtTheAttemptCap()
    {
        await using var fixture = await OutboxFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        await fixture.InsertAsync("retry", fixture.Clock.GetCurrentInstant());
        var sender = new RetryableFailureSender();

        await fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None);
        fixture.Clock.Advance(Duration.FromHours(2));
        await fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None);
        fixture.Clock.Advance(Duration.FromHours(2));
        await fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None);
        fixture.Clock.Advance(Duration.FromHours(2));
        var afterCap = await fixture.CreateWorker(sender).ProcessAsync(CancellationToken.None);

        Assert.Equal("failed", await fixture.StatusAsync("retry"));
        Assert.Equal(3, await fixture.AttemptsAsync("retry"));
        Assert.Equal(3, sender.Keys.Count);
        Assert.Equal(0, afterCap.Claimed);
    }

    private sealed class OutboxFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        public bool HasDatabase => !string.IsNullOrWhiteSpace(_connectionString);
        public FakeClock Clock { get; } = new(Instant.FromUtc(2026, 8, 16, 12, 0));

        private OutboxFixture(string connectionString) => _connectionString = connectionString;

        public static async Task<OutboxFixture> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
            if (string.IsNullOrWhiteSpace(connectionString)) return new OutboxFixture(string.Empty);

            var fixture = new OutboxFixture(connectionString);
            await fixture.ExecuteAsync("DELETE FROM email_outbox;");
            return fixture;
        }

        public EmailOutboxWorker CreateWorker(IEmailSender sender) => new(
            Options.Create(new DatabaseOptions { ConnectionString = _connectionString }),
            Options.Create(new OutboxOptions { BatchSize = 10, MaximumAttempts = 3, LeaseMinutes = 5 }),
            sender,
            Clock,
            NullLogger<EmailOutboxWorker>.Instance);

        public async Task InsertAsync(string key, Instant dueAt, string? shareUrl = null)
        {
            if (string.IsNullOrEmpty(_connectionString)) return;
            var payload = JsonSerializer.Serialize(new
            {
                to = "recipient@example.test",
                subject = "A medical image has been shared with you",
                html = shareUrl is null ? "<p>Reminder.</p>" : $"<p><a href=\"{shareUrl}\">Open link</a></p>",
                text = shareUrl is null ? "Reminder." : shareUrl,
                shareUrl
            });
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO email_outbox (id, kind, payload, due_at, status, idempotency_key)
                VALUES (@id, 'share', CAST(@payload AS jsonb), @due_at, 'pending', @idempotency_key);
                """, connection);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("payload", payload);
            command.Parameters.AddWithValue("due_at", dueAt.ToDateTimeOffset());
            command.Parameters.AddWithValue("idempotency_key", $"outbox/{key}");
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> StatusAsync(string key) => await ScalarAsync<string>("SELECT status FROM email_outbox WHERE idempotency_key = @key;", $"outbox/{key}");
        public async Task<string> PayloadAsync(string key) => await ScalarAsync<string>("SELECT payload::text FROM email_outbox WHERE idempotency_key = @key;", $"outbox/{key}");
        public async Task<int> AttemptsAsync(string key) => await ScalarAsync<int>("SELECT attempts FROM email_outbox WHERE idempotency_key = @key;", $"outbox/{key}");
        public async Task<int> CountByStatusAsync(string status) => await ScalarAsync<int>("SELECT count(*)::int FROM email_outbox WHERE status = @key;", status);

        private async Task<T> ScalarAsync<T>(string sql, string key)
        {
            if (string.IsNullOrEmpty(_connectionString)) return default!;
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("key", key);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class RecordingSender : IEmailSender
    {
        public ConcurrentBag<string> Keys { get; } = [];
        public virtual Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Keys.Add(message.IdempotencyKey);
            return Task.FromResult(EmailSendResult.Sent($"provider_{message.IdempotencyKey}"));
        }
    }

    private sealed class BlockingSender : RecordingSender
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Keys.Add(message.IdempotencyKey);
            FirstSendStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return EmailSendResult.Sent($"provider_{message.IdempotencyKey}");
        }

        public void ReleaseFirstSend() => _release.SetResult();
    }

    private sealed class AcceptThenCrashSender : IEmailSender
    {
        public List<string> Keys { get; } = [];
        public HashSet<string> ProviderCommands { get; } = [];
        private bool _crashed;

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Keys.Add(message.IdempotencyKey);
            ProviderCommands.Add(message.IdempotencyKey);
            if (!_crashed)
            {
                _crashed = true;
                throw new OperationCanceledException("simulated process crash after provider acceptance");
            }

            return Task.FromResult(EmailSendResult.Sent($"provider_{message.IdempotencyKey}"));
        }
    }

    private sealed class RetryableFailureSender : RecordingSender
    {
        public override Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Keys.Add(message.IdempotencyKey);
            return Task.FromResult(EmailSendResult.Failed(new EmailSendFailure(EmailFailureKind.Network, "test_network_error", true)));
        }
    }
}

[CollectionDefinition("email-outbox", DisableParallelization = true)]
public sealed class EmailOutboxCollection;
