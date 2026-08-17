using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using NodaTime.Testing;
using PatientDataPortal.Api.Scheduling;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class ProviderAppointmentsEndpointTests
{
    [Fact]
    public async Task ProviderScheduleIsScopedToTheAuthenticatedProvider()
    {
        await using var factory = new AppointmentsApplicationFactory(AppRole.Provider);
        using var client = AuthorizedClient(factory);

        var response = await client.GetAsync("/api/provider/appointments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AppointmentsApplicationFactory.UserId, factory.Appointments.UserId);
        var schedule = await response.Content.ReadFromJsonAsync<ProviderAppointmentSchedule>();
        Assert.NotNull(schedule);
        Assert.Equal("America/Chicago", schedule.TimeZoneId);
        Assert.Single(schedule.Upcoming);
        Assert.Single(schedule.Past);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "provider_appointment_schedule_viewed" && audit.TargetType == "appointment" && audit.Result == "allowed");
    }

    [Fact]
    public async Task NonProvidersCannotReadScheduleOrRunStatusActions()
    {
        await using var factory = new AppointmentsApplicationFactory(AppRole.Patient);
        using var client = AuthorizedClient(factory);

        var schedule = await client.GetAsync("/api/provider/appointments");
        var action = await client.PatchAsJsonAsync($"/api/appointments/{Guid.NewGuid()}/status", new { status = "completed" });

        Assert.Equal(HttpStatusCode.Forbidden, schedule.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, action.StatusCode);
        Assert.Null(factory.Appointments.UserId);
        Assert.Null(factory.Lifecycle.Request);
    }

    [Fact]
    public async Task ProviderStatusActionUsesTheExistingLifecycleEndpoint()
    {
        await using var factory = new AppointmentsApplicationFactory(AppRole.Provider);
        using var client = AuthorizedClient(factory);
        var appointmentId = Guid.NewGuid();

        var response = await client.PatchAsJsonAsync($"/api/appointments/{appointmentId}/status", new { status = "no-show" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((AppointmentsApplicationFactory.UserId, AppRole.Provider, appointmentId, "no-show"), factory.Lifecycle.Request);
    }

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "valid"); return client;
    }

    private sealed class AppointmentsApplicationFactory(AppRole role) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("735f86b0-8f47-41d3-9c6d-6f5a3822eaa4");
        public FakeAppointments Appointments { get; } = new();
        public FakeLifecycle Lifecycle { get; } = new();
        public CapturingAudit Audit { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IProviderAppointmentsRepository>(); services.RemoveAll<IAppointmentLifecycleService>(); services.RemoveAll<IClock>(); services.RemoveAll<IAuditWriter>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier()); services.AddSingleton<IUserProfileRoleRepository>(new FakeRoles(role)); services.AddSingleton<IProviderAppointmentsRepository>(Appointments); services.AddSingleton<IAppointmentLifecycleService>(Lifecycle); services.AddSingleton<IClock>(new FakeClock(Instant.FromUtc(2030, 1, 1, 0, 0))); services.AddSingleton<IAuditWriter>(Audit);
        });
    }

    private sealed class FakeVerifier : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(AppointmentsApplicationFactory.UserId, true) : null); }
    private sealed class FakeRoles(AppRole role) : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(role); }
    private sealed class FakeAppointments : IProviderAppointmentsRepository
    {
        public Guid? UserId { get; private set; }
        public Task<ProviderAppointmentSchedule?> ListAsync(Guid userId, Instant now, CancellationToken cancellationToken)
        {
            UserId = userId;
            return Task.FromResult<ProviderAppointmentSchedule?>(new("America/Chicago", [new(Guid.NewGuid(), new DateTimeOffset(2030, 1, 2, 15, 0, 0, TimeSpan.Zero), "Follow-up", "confirmed")], [new(Guid.NewGuid(), new DateTimeOffset(2029, 12, 31, 15, 0, 0, TimeSpan.Zero), "Consultation", "completed")]));
        }
    }
    private sealed class FakeLifecycle : IAppointmentLifecycleService
    {
        public (Guid UserId, AppRole Role, Guid AppointmentId, string Status)? Request { get; private set; }
        public Task<AppointmentStatusConfirmation> TransitionAsync(Guid actorUserId, AppRole actorRole, Guid appointmentId, string status, CancellationToken cancellationToken) { Request = (actorUserId, actorRole, appointmentId, status); return Task.FromResult(new AppointmentStatusConfirmation(appointmentId, status)); }
    }
    private sealed class CapturingAudit : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
    }
}
