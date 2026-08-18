using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Identity;
using Xunit;

namespace PatientDataPortal.Api.Tests;

[Trait("Category", "Integration")]
[Collection("identity-verification")]
public sealed class IdentityVerificationServiceTests
{
    [Fact]
    public async Task CorrectPair_ClaimsOnlyThatRecord_AndWritesAllowedAudit()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync(); var account = Guid.NewGuid();

        var result = await fixture.Service.VerifyAsync(account, true, new(record.Reference, "1980-01-02"), "198.51.100.7", default);

        Assert.True(result.Succeeded);
        Assert.True(await fixture.Service.IsVerifiedPatientAsync(account, default));
        Assert.Equal(account, await fixture.ClaimedByAsync(record.Id));
        Assert.Equal("patient", await fixture.RoleAsync(account));
        Assert.Equal(1, await fixture.CountAsync("patient_claim_events", "patient_record_id", record.Id));
        Assert.Equal(1, await fixture.CountTextAsync("audit_log", "target_reference", fixture.ReferenceHmac(record.Reference)));
    }

    [Fact]
    public async Task WrongReferenceWrongDobAndBoth_HaveTheSameFailureOutcome()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync();

        var wrongReference = await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new("PTDP-not-a-record", "1980-01-02"), "198.51.100.1", default);
        var wrongDob = await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1981-01-02"), "198.51.100.2", default);
        var bothWrong = await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new("PTDP-not-a-record", "1981-01-02"), "198.51.100.3", default);

        Assert.False(wrongReference.Succeeded); Assert.False(wrongDob.Succeeded); Assert.False(bothWrong.Succeeded);
    }

    [Fact]
    public async Task UnconfirmedAccountCannotClaim_ButConfirmedAccountCan()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync(); var account = Guid.NewGuid();

        Assert.False((await fixture.Service.VerifyAsync(account, false, new(record.Reference, "1980-01-02"), "198.51.100.4", default)).Succeeded);
        Assert.True((await fixture.Service.VerifyAsync(account, true, new(record.Reference, "1980-01-02"), "198.51.100.4", default)).Succeeded);
    }

    [Fact]
    public async Task ExistingNonPatientRoleCannotClaimPatientRecord()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync(); var account = Guid.NewGuid();
        await fixture.AddProfileAsync(account, "provider");

        var result = await fixture.Service.VerifyAsync(account, true, new(record.Reference, "1980-01-02"), "198.51.100.44", default);

        Assert.False(result.Succeeded);
        Assert.Null(await fixture.ClaimedByAsync(record.Id));
        Assert.Equal("provider", await fixture.RoleAsync(account));
    }

    [Fact]
    public async Task FifthFailureLocksTheAccount_DurablyAcrossServiceInstances()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync(); var account = Guid.NewGuid();
        for (var attempt = 0; attempt < 5; attempt++) Assert.False((await fixture.Service.VerifyAsync(account, true, new(record.Reference, "1999-01-01"), "198.51.100.5", default)).Succeeded);

        var afterRestart = fixture.CreateService();
        Assert.False((await afterRestart.VerifyAsync(account, true, new(record.Reference, "1980-01-02"), "198.51.100.5", default)).Succeeded);
        Assert.Equal("locked", await fixture.LatestAttemptResultAsync(account));
    }

    [Fact]
    public async Task LockoutExpiresWhenTheInjectedClockPassesTheFifteenMinuteWindow()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync(); var account = Guid.NewGuid();
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.False((await fixture.Service.VerifyAsync(account, true, new(record.Reference, "1999-01-01"), "198.51.100.50", default)).Succeeded);

        fixture.Advance(Duration.FromMinutes(15));

        Assert.True((await fixture.Service.VerifyAsync(account, true, new(record.Reference, "1980-01-02"), "198.51.100.50", default)).Succeeded);
    }

    [Fact]
    public async Task DistributedReferenceAttackIsSlowedWithoutLockingTheLegitimateAccount()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync();
        for (var attacker = 0; attacker < 10; attacker++)
            await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1999-01-01"), $"198.51.100.{attacker}", default);

        var legitimate = await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1980-01-02"), "203.0.113.9", default);
        Assert.True(legitimate.Succeeded);
        Assert.True(legitimate.ThrottleDelay > Duration.Zero);
    }

    [Fact]
    public async Task ThrottleWindowExpiresWhenTheInjectedClockAdvances()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync();
        for (var attacker = 0; attacker < 10; attacker++)
            await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1999-01-01"), "198.51.100.60", default);

        Assert.True((await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1999-01-01"), "198.51.100.60", default)).ThrottleDelay > Duration.Zero);
        fixture.Advance(Duration.FromMinutes(15));
        Assert.Equal(Duration.Zero, (await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1999-01-01"), "198.51.100.60", default)).ThrottleDelay);
    }

    [Fact]
    public async Task ClaimedRecordIsGenericFailure_AndAdminRecoveryPermitsReclaim()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync();
        Assert.True((await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1980-01-02"), "198.51.100.6", default)).Succeeded);
        Assert.False((await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1980-01-02"), "198.51.100.8", default)).Succeeded);

        await fixture.Service.RecoverClaimAsync(record.Id, Guid.NewGuid(), "support_review", default);
        Assert.True((await fixture.Service.VerifyAsync(Guid.NewGuid(), true, new(record.Reference, "1980-01-02"), "198.51.100.9", default)).Succeeded);
        Assert.Equal(4, await fixture.CountAsync("patient_claim_events", "patient_record_id", record.Id));
    }

    [Fact]
    public async Task AttemptsRetainOnlyHmacScopedNetworkAndReferenceValues()
    {
        await using var fixture = await IdentityFixture.CreateAsync(); if (!fixture.HasDatabase) return;
        var record = await fixture.AddPatientAsync(); const string network = "198.51.100.42"; var account = Guid.NewGuid();
        await fixture.Service.VerifyAsync(account, true, new(record.Reference, "1999-01-01"), network, default);

        var (storedNetwork, storedReference) = await fixture.AttemptKeysAsync(account);
        Assert.NotEqual(network, storedNetwork); Assert.NotEqual(record.Reference, storedReference);
        Assert.Equal(fixture.ReferenceHmac(record.Reference), storedReference);
    }

    private sealed class IdentityFixture : IAsyncDisposable
    {
        private const string HmacKey = "test-only-hmac-key";
        private readonly string _connectionString;
        private readonly FakeClock _clock = new(Instant.FromUtc(2026, 8, 16, 12, 0));
        public bool HasDatabase => !string.IsNullOrWhiteSpace(_connectionString);
        public IdentityVerificationService Service => CreateService();
        private IdentityFixture(string connectionString) => _connectionString = connectionString;
        public static Task<IdentityFixture> CreateAsync() => Task.FromResult(new IdentityFixture(Environment.GetEnvironmentVariable("DATABASE_URL") ?? string.Empty));
        public IdentityVerificationService CreateService() => new(Options.Create(new DatabaseOptions { ConnectionString = _connectionString }), Options.Create(new IdentityVerificationOptions { HmacKey = HmacKey }), _clock);
        public void Advance(Duration duration) => _clock.Advance(duration);
        public string ReferenceHmac(string reference) => Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(HmacKey), System.Text.Encoding.UTF8.GetBytes(reference)));
        public async Task<(Guid Id, string Reference)> AddPatientAsync()
        {
            var id = Guid.NewGuid(); var reference = "PTDP-" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync();
            await using var command = new NpgsqlCommand("INSERT INTO patient_records (id, patient_ref, dob, full_name) VALUES (@id, @reference, '1980-01-02', 'Test Patient')", connection);
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("reference", reference); await command.ExecuteNonQueryAsync(); return (id, reference);
        }
        public async Task AddProfileAsync(Guid account, string role) { await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(); await using var cmd = new NpgsqlCommand("INSERT INTO user_profiles (user_id, role, display_name, tz) VALUES (@account, @role, 'Test User', 'UTC')", c); cmd.Parameters.AddWithValue("account", account); cmd.Parameters.AddWithValue("role", role); await cmd.ExecuteNonQueryAsync(); }
        public async Task<Guid?> ClaimedByAsync(Guid id) { await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(); await using var cmd = new NpgsqlCommand("SELECT claimed_by FROM patient_records WHERE id = @id", c); cmd.Parameters.AddWithValue("id", id); return await cmd.ExecuteScalarAsync() is Guid value ? value : null; }
        public async Task<string?> RoleAsync(Guid account) { await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(); await using var cmd = new NpgsqlCommand("SELECT role FROM user_profiles WHERE user_id = @account", c); cmd.Parameters.AddWithValue("account", account); return await cmd.ExecuteScalarAsync() as string; }
        public async Task<int> CountAsync(string table, string column, Guid id) { await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(); await using var cmd = new NpgsqlCommand($"SELECT count(*)::int FROM {table} WHERE {column} = @id", c); cmd.Parameters.AddWithValue("id", id.ToString()); if (column == "patient_record_id") cmd.Parameters["id"].Value = id; return (int)(await cmd.ExecuteScalarAsync())!; }
        public async Task<int> CountTextAsync(string table, string column, string value) { await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(); await using var cmd = new NpgsqlCommand($"SELECT count(*)::int FROM {table} WHERE {column} = @value", c); cmd.Parameters.AddWithValue("value", value); return (int)(await cmd.ExecuteScalarAsync())!; }
        public async Task<string> LatestAttemptResultAsync(Guid account) { await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(); await using var cmd = new NpgsqlCommand("SELECT result FROM verification_attempts WHERE account_id = @account ORDER BY attempted_at DESC LIMIT 1", c); cmd.Parameters.AddWithValue("account", account); return (string)(await cmd.ExecuteScalarAsync())!; }
        public async Task<(string Network, string Reference)> AttemptKeysAsync(Guid account) { await using var c = new NpgsqlConnection(_connectionString); await c.OpenAsync(); await using var cmd = new NpgsqlCommand("SELECT network_hmac, patient_ref_hmac FROM verification_attempts WHERE account_id = @account ORDER BY attempted_at DESC LIMIT 1", c); cmd.Parameters.AddWithValue("account", account); await using var reader = await cmd.ExecuteReaderAsync(); await reader.ReadAsync(); return (reader.GetString(0), reader.GetString(1)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[CollectionDefinition("identity-verification", DisableParallelization = true)]
public sealed class IdentityVerificationCollection;
