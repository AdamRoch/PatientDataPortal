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
        Assert.Equal(1, await fixture.CountAppointmentsForSlotAsync(slot));
        Assert.Equal(2, await fixture.CountAppointmentEventsAsync(first.Id));
        Assert.Equal(1, await fixture.CountRemindersAsync(first.Id));
        Assert.Equal(1, await fixture.CountAppointmentAuditsAsync(first.Id));
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
        Assert.Equal(1, await fixture.CountAppointmentsForSlotAsync(slot));
        Assert.Equal("booked", await fixture.SlotStatusAsync(slot));
    }

    [Fact]
    public async Task SimultaneousRequestsForTheLastOpenSlotProduceOneCreatedAppointmentAndConflicts()
    {
        const int attemptCount = 8;
        await using var fixture = await AppointmentFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var slot = await fixture.SeedSlotAsync();
        await using var factory = new BookingConcurrencyApplicationFactory(fixture.PatientId);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        var releaseRequests = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var requests = Enumerable.Range(0, attemptCount).Select(async attempt =>
        {
            await releaseRequests.Task;
            return await client.PostAsJsonAsync("/api/appointments", new
            {
                slotId = slot,
                serviceId = fixture.ServiceId,
                idempotencyKey = $"concurrent-{attempt}"
            });
        }).ToArray();

        releaseRequests.SetResult(true);
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            var conflicts = responses.Where(response => response.StatusCode == HttpStatusCode.Conflict).ToArray();
            Assert.Equal(attemptCount - 1, conflicts.Length);
            await Task.WhenAll(conflicts.Select(async response =>
            {
                var error = await response.Content.ReadFromJsonAsync<BookingError>();
                Assert.NotNull(error);
                Assert.Equal("slot_no_longer_available", error.Error);
            }));
            Assert.Equal(1, await fixture.CountAppointmentsForSlotAsync(slot));
            Assert.Equal("booked", await fixture.SlotStatusAsync(slot));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
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
        Assert.Equal(0, await fixture.CountAppointmentsForSlotAsync(slot));
        Assert.Equal(0, await fixture.CountAppointmentEventsForSlotAsync(slot));
        Assert.Equal(0, await fixture.CountRemindersForSlotAsync(slot));
        Assert.Equal(0, await fixture.CountAppointmentAuditsForSlotAsync(slot));
    }

    [Fact]
    public async Task RescheduleAtomicallyMovesTheAppointmentAndReplacesItsReminder()
    {
        await using var fixture = await AppointmentFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var oldSlot = await fixture.SeedSlotAsync();
        var newSlot = await fixture.SeedOpenSlotAsync("2030-01-03T09:00:00Z");
        var appointment = await fixture.Service.BookAsync(fixture.PatientId, new(oldSlot, fixture.ServiceId, "move-me"), default);

        var changed = await fixture.Changes.RescheduleAsync(fixture.PatientId, appointment.Id, new(newSlot), default);

        Assert.Equal(newSlot, changed.SlotId);
        Assert.Equal(2, changed.ScheduleVersion);
        Assert.Equal("open", await fixture.SlotStatusAsync(oldSlot));
        Assert.Equal("booked", await fixture.SlotStatusAsync(newSlot));
        Assert.Equal((newSlot, 2, "confirmed"), await fixture.AppointmentStateAsync(appointment.Id));
        Assert.Equal(new[] { (1, "superseded"), (2, "pending") }, await fixture.ReminderStatesAsync(appointment.Id));
    }

    [Fact]
    public async Task RescheduleFailureRollsBackAndKeepsTheOriginalReminderPending()
    {
        await using var fixture = await AppointmentFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var oldSlot = await fixture.SeedSlotAsync();
        var unavailableSlot = await fixture.SeedOpenSlotAsync("2030-01-03T09:00:00Z");
        var appointment = await fixture.Service.BookAsync(fixture.PatientId, new(oldSlot, fixture.ServiceId, "original"), default);
        await fixture.Service.BookAsync(Guid.NewGuid(), new(unavailableSlot, fixture.ServiceId, "already-booked"), default);

        var exception = await Assert.ThrowsAsync<PatientDataPortal.Api.Errors.DomainException>(() => fixture.Changes.RescheduleAsync(fixture.PatientId, appointment.Id, new(unavailableSlot), default));

        Assert.Equal("slot_no_longer_available", exception.Code);
        Assert.Equal("booked", await fixture.SlotStatusAsync(oldSlot));
        Assert.Equal((oldSlot, 1, "confirmed"), await fixture.AppointmentStateAsync(appointment.Id));
        Assert.Equal(new[] { (1, "pending") }, await fixture.ReminderStatesAsync(appointment.Id));
    }

    [Fact]
    public async Task CancelFreesTheSlotAndSupersedesTheReminderWithoutReplacement()
    {
        await using var fixture = await AppointmentFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var slot = await fixture.SeedSlotAsync();
        var appointment = await fixture.Service.BookAsync(fixture.PatientId, new(slot, fixture.ServiceId, "cancel-me"), default);

        await fixture.Changes.CancelAsync(fixture.PatientId, appointment.Id, default);

        Assert.Equal("open", await fixture.SlotStatusAsync(slot));
        Assert.Equal((slot, 1, "cancelled"), await fixture.AppointmentStateAsync(appointment.Id));
        Assert.Equal(new[] { (1, "superseded") }, await fixture.ReminderStatesAsync(appointment.Id));
    }

    [Fact]
    public async Task ChangesRequirePatientOwnershipAndTwentyFourHoursNotice()
    {
        await using var fixture = await AppointmentFixture.CreateAsync();
        if (!fixture.HasDatabase) return;
        var slot = await fixture.SeedSlotAsync();
        var appointment = await fixture.Service.BookAsync(fixture.PatientId, new(slot, fixture.ServiceId, "owned"), default);

        var ownership = await Assert.ThrowsAsync<PatientDataPortal.Api.Errors.DomainException>(() => fixture.Changes.CancelAsync(Guid.NewGuid(), appointment.Id, default));
        Assert.Equal("appointment_not_found", ownership.Code);
        await fixture.SetSlotStartAsync(slot, "2030-01-01T23:59:59Z");
        await fixture.SetAppointmentStartAsync(appointment.Id, "2030-01-01T23:59:59Z");
        var notice = await Assert.ThrowsAsync<PatientDataPortal.Api.Errors.DomainException>(() => fixture.Changes.CancelAsync(fixture.PatientId, appointment.Id, default));
        Assert.Equal("minimum_notice_required", notice.Code);
        Assert.Equal("booked", await fixture.SlotStatusAsync(slot));
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
    private sealed record BookingError(string Error);

    private sealed class AppointmentApplicationFactory : WebApplicationFactory<Program>
    {
        public static readonly Guid PatientId = Guid.Parse("83f17226-74be-4f30-96d8-4501a611d8f9");
        public static readonly Guid SlotId = Guid.Parse("4c75b645-66ec-4ec2-9cb8-c5ae2bd55dc0");
        public static readonly Guid ServiceId = Guid.Parse("75487efd-8f43-4d02-9877-aa66d0e77bfe");
        public FakeBookings Bookings { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IAppointmentBookingService>(); services.RemoveAll<IAppointmentChangeService>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier(PatientId)); services.AddSingleton<IUserProfileRoleRepository>(new FakeRoles()); services.AddSingleton<IAppointmentBookingService>(Bookings); services.AddSingleton<IAppointmentChangeService>(new FakeChanges());
        });
    }

    private sealed class BookingConcurrencyApplicationFactory(Guid patientId) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier(patientId)); services.AddSingleton<IUserProfileRoleRepository>(new FakeRoles());
        });
    }

    private sealed class FakeVerifier(Guid patientId) : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult<AuthenticatedUser?>(token == "valid" ? new(patientId, true) : null); }
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
    private sealed class FakeChanges : IAppointmentChangeService
    {
        public Task<AppointmentChangeConfirmation> RescheduleAsync(Guid patientUserId, Guid appointmentId, RescheduleAppointmentRequest request, CancellationToken cancellationToken) => Task.FromResult(new AppointmentChangeConfirmation(appointmentId, request.SlotId, DateTimeOffset.UtcNow, 2, "confirmed"));
        public Task CancelAsync(Guid patientUserId, Guid appointmentId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AppointmentFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        public Guid PatientId { get; } = Guid.NewGuid();
        public Guid ProviderId { get; } = Guid.NewGuid();
        public Guid ServiceId { get; } = Guid.NewGuid();
        public bool HasDatabase => !string.IsNullOrWhiteSpace(_connectionString);
        public AppointmentBookingService Service { get; }
        public AppointmentChangeService Changes { get; }
        private AppointmentFixture(string connectionString)
        {
            _connectionString = connectionString;
            Service = new AppointmentBookingService(Options.Create(new DatabaseOptions { ConnectionString = connectionString }), new FakeClock(Instant.FromUtc(2030, 1, 1, 0, 0)));
            Changes = new AppointmentChangeService(Options.Create(new DatabaseOptions { ConnectionString = connectionString }), new FakeClock(Instant.FromUtc(2030, 1, 1, 0, 0)));
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
        public async Task<Guid> SeedOpenSlotAsync(string startsAt)
        {
            var slot = Guid.NewGuid(); var start = DateTimeOffset.Parse(startsAt);
            await ExecuteAsync("INSERT INTO slots (id, provider_id, start_at, end_at, status) VALUES (@id, @provider, @start, @end, 'open')", ("id", slot), ("provider", ProviderId), ("start", start), ("end", start.AddMinutes(30)));
            return slot;
        }
        public Task<int> CountAppointmentsForSlotAsync(Guid slot) => ScalarAsync<int>("SELECT count(*)::int FROM appointments WHERE slot_id = @slot", ("slot", slot));
        public Task<int> CountAppointmentEventsAsync(Guid appointmentId) => ScalarAsync<int>("SELECT count(*)::int FROM appointment_events WHERE appointment_id = @appointment", ("appointment", appointmentId));
        public Task<int> CountRemindersAsync(Guid appointmentId) => ScalarAsync<int>("SELECT count(*)::int FROM email_outbox WHERE appointment_id = @appointment", ("appointment", appointmentId));
        public Task<int> CountAppointmentAuditsAsync(Guid appointmentId) => ScalarAsync<int>("SELECT count(*)::int FROM audit_log WHERE target_type = 'appointment' AND target_reference = @appointment", ("appointment", appointmentId.ToString()));
        public Task<int> CountAppointmentEventsForSlotAsync(Guid slot) => ScalarAsync<int>("SELECT count(*)::int FROM appointment_events WHERE appointment_id IN (SELECT id FROM appointments WHERE slot_id = @slot)", ("slot", slot));
        public Task<int> CountRemindersForSlotAsync(Guid slot) => ScalarAsync<int>("SELECT count(*)::int FROM email_outbox WHERE appointment_id IN (SELECT id FROM appointments WHERE slot_id = @slot)", ("slot", slot));
        public Task<int> CountAppointmentAuditsForSlotAsync(Guid slot) => ScalarAsync<int>("SELECT count(*)::int FROM audit_log WHERE target_type = 'appointment' AND target_reference IN (SELECT id::text FROM appointments WHERE slot_id = @slot)", ("slot", slot));
        public Task<string> SlotStatusAsync(Guid slot) => ScalarAsync<string>("SELECT status FROM slots WHERE id = @id", ("id", slot));
        public Task SetSlotStartAsync(Guid slot, string startsAt) => ExecuteAsync("UPDATE slots SET start_at = @start, end_at = @end WHERE id = @id", ("id", slot), ("start", DateTimeOffset.Parse(startsAt)), ("end", DateTimeOffset.Parse(startsAt).AddMinutes(30)));
        public Task SetAppointmentStartAsync(Guid appointmentId, string startsAt) => ExecuteAsync("UPDATE appointments SET start_at = @start WHERE id = @id", ("id", appointmentId), ("start", DateTimeOffset.Parse(startsAt)));
        public async Task<(Guid SlotId, int ScheduleVersion, string Status)> AppointmentStateAsync(Guid appointmentId)
        {
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand("SELECT slot_id, schedule_version, status FROM appointments WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", appointmentId); await using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
            return (reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2));
        }
        public async Task<(int ScheduleVersion, string Status)[]> ReminderStatesAsync(Guid appointmentId)
        {
            await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand("SELECT schedule_version, status FROM email_outbox WHERE appointment_id = @id ORDER BY schedule_version", connection);
            command.Parameters.AddWithValue("id", appointmentId); await using var reader = await command.ExecuteReaderAsync(); var results = new List<(int, string)>();
            while (await reader.ReadAsync()) results.Add((reader.GetInt32(0), reader.GetString(1)));
            return results.ToArray();
        }
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
