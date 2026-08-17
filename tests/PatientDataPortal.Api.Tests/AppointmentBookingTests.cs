using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

[Trait("Category", "Integration")]
[Collection("appointment-booking")]
public sealed class AppointmentBookingTests
{
    [Fact]
    public async Task DuplicateKeyReturnsTheOriginalConfirmationAndOneAtomicSetOfRows()
    {
        await using var fixture = await AppointmentFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var slot = await fixture.SeedSlotAsync();
        var request = new CreateAppointmentRequest(slot, fixture.ServiceId, "retry-key");

        var first = await fixture.Service.BookAsync(fixture.PatientId, request, default);
        var retry = await fixture.Service.BookAsync(fixture.PatientId, request, default);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(1, await fixture.CountAsync("appointments"));
        Assert.Equal(2, await fixture.CountAsync("appointment_events"));
        Assert.Equal(1, await fixture.CountAsync("email_outbox"));
        Assert.Equal(1, await fixture.CountAsync("audit_log"));
        Assert.Equal("booked", await fixture.SlotStatusAsync(slot));
    }

    [Fact]
    public async Task CompetingKeysForOneSlotProduceOneAppointmentAndOneConflict()
    {
        await using var fixture = await AppointmentFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var slot = await fixture.SeedSlotAsync();
        var first = fixture.Service.BookAsync(fixture.PatientId, new(slot, fixture.ServiceId, "first"), default);
        var second = fixture.Service.BookAsync(Guid.NewGuid(), new(slot, fixture.ServiceId, "second"), default);
        var outcomes = await Task.WhenAll(Wrap(first), Wrap(second));

        Assert.Single(outcomes, result => result.Success);
        Assert.Single(outcomes, result => !result.Success && result.ErrorCode == "slot_no_longer_available");
        Assert.Equal(1, await fixture.CountAsync("appointments"));
        Assert.Equal("booked", await fixture.SlotStatusAsync(slot));
    }

    [Fact]
    public async Task InvalidServiceRollsBackTheSlotAndAllDependentWrites()
    {
        await using var fixture = await AppointmentFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var slot = await fixture.SeedSlotAsync();

        var exception = await Assert.ThrowsAsync<PatientDataPortal.Api.Errors.DomainException>(() => fixture.Service.BookAsync(fixture.PatientId, new(slot, Guid.NewGuid(), "bad-service"), default));

        Assert.Equal("invalid_service", exception.Code);
        Assert.Equal("open", await fixture.SlotStatusAsync(slot));
        Assert.Equal(0, await fixture.CountAsync("appointments"));
        Assert.Equal(0, await fixture.CountAsync("appointment_events"));
        Assert.Equal(0, await fixture.CountAsync("email_outbox"));
        Assert.Equal(0, await fixture.CountAsync("audit_log"));
    }

    [Fact]
    public async Task PatientEndpointReturnsConfirmationAndBoundedTiming()
    {
        await using var factory = new AppointmentApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        var response = await client.PostAsJsonAsync("/api/appointments", new { slotId = AppointmentApplicationFactory.SlotId, serviceId = AppointmentApplicationFactory.ServiceId, idempotencyKey = "request-1" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Server-Timing", out var values));
        Assert.Matches("^booking;dur=[0-9]+\\.[0-9]$", Assert.Single(values));
        Assert.Equal("request-1", factory.Bookings.Request!.IdempotencyKey);
    }

    private static async Task<BookingOutcome> Wrap(Task<AppointmentConfirmation> task)
    {
        try { await task; return new(true, null); }
        catch (PatientDataPortal.Api.Errors.DomainException exception) { return new(false, exception.Code); }
    }

    private sealed record BookingOutcome(bool Success, string? ErrorCode);

    private sealed class AppointmentApplicationFactory : WebApplicationFactory<Program>
    {
        public static readonly Guid PatientId = Guid.Parse("83f17226-74be-4f30-96d8-4501a611d8f9");
        public static readonly Guid SlotId = Guid.Parse("4c75b645-66ec-4ec2-9cb8-c5ae2bd55dc0");
        public static readonly Guid ServiceId = Guid.Parse("75487efd-8f43-4d02-9877-aa66d0e77bfe");
        public FakeBookings Bookings { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IAppointmentBookingService>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier()); services.AddSingleton<IUserProfileRoleRepository>(new FakeRoles()); services.AddSingleton<IAppointmentBookingService>(Bookings);
        });
    }

    private sealed class FakeVerifier : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult<AuthenticatedUser?>(token == "valid" ? new(AppointmentApplicationFactory.PatientId, true) : null); }
    private sealed class FakeRoles : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient); }
    private sealed class FakeBookings : IAppointmentBookingService
    {
        public CreateAppointmentRequest? Request { get; private set; }
        public Task<AppointmentConfirmation> BookAsync(Guid patientUserId, CreateAppointmentRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new AppointmentConfirmation(Guid.NewGuid(), request.SlotId, Guid.NewGuid(), request.ServiceId, DateTimeOffset.UtcNow, 1, "confirmed"));
        }
    }

    private sealed class AppointmentFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        public Guid PatientId { get; } = Guid.NewGuid();
        public Guid ProviderId { get; } = Guid.NewGuid();
        public Guid ServiceId { get; } = Guid.NewGuid();
        public bool HasDatabase => !string.IsNullOrWhiteSpace(_connectionString);
        public AppointmentBookingService Service { get; }
        private AppointmentFixture(string connectionString)
        {
            _connectionString = connectionString;
            Service = new AppointmentBookingService(Options.Create(new DatabaseOptions { ConnectionString = connectionString }), new FakeClock(Instant.FromUtc(2030, 1, 1, 0, 0)));
        }
        public static Task<AppointmentFixture> CreateAsync() => Task.FromResult(new AppointmentFixture(Environment.GetEnvironmentVariable("DATABASE_URL") ?? string.Empty));
        public async Task<Guid> SeedSlotAsync()
        {
            var providerUser = Guid.NewGuid(); var slot = Guid.NewGuid(); var startsAt = DateTimeOffset.Parse("2030-01-02T09:00:00Z");
            await ExecuteAsync("INSERT INTO user_profiles (user_id, role, display_name, tz) VALUES (@id, 'provider', 'Test provider', 'UTC')", ("id", providerUser));
            await ExecuteAsync("INSERT INTO providers (id, user_id, tz, slot_length_min) VALUES (@id, @user, 'UTC', 30)", ("id", ProviderId), ("user", providerUser));
            await ExecuteAsync("INSERT INTO services (id, provider_id, name) VALUES (@id, @provider, 'Test service')", ("id", ServiceId), ("provider", ProviderId));
            await ExecuteAsync("INSERT INTO slots (id, provider_id, start_at, end_at, status) VALUES (@id, @provider, @start, @end, 'open')", ("id", slot), ("provider", ProviderId), ("start", startsAt), ("end", startsAt.AddMinutes(30)));
            return slot;
        }
        public Task<int> CountAsync(string table) => ScalarAsync<int>($"SELECT count(*)::int FROM {table}");
        public Task<string> SlotStatusAsync(Guid slot) => ScalarAsync<string>("SELECT status FROM slots WHERE id = @id", ("id", slot));
        private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand(sql, connection);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return (T)(await command.ExecuteScalarAsync())!;
        }
        private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand(sql, connection);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[CollectionDefinition("appointment-booking", DisableParallelization = true)]
public sealed class AppointmentBookingCollection;
