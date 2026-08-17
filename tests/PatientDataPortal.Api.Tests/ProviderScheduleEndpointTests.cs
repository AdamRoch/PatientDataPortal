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

public sealed class ProviderScheduleEndpointTests
{
    [Fact]
    public async Task ProviderCanSetWeekdayHoursSlotLengthBlockedTimeAndService()
    {
        await using var factory = new ScheduleApplicationFactory(AppRole.Provider);
        using var client = AuthorizedClient(factory);
        var hours = await client.PutAsJsonAsync("/api/provider/schedule/working-hours", new { rules = Enumerable.Range(1, 5).Select(weekday => new { weekday, localStart = "09:00", localEnd = "17:00" }) });
        var length = await client.PutAsJsonAsync("/api/provider/schedule/slot-length", new { slotLengthMinutes = 30 });
        var blocked = await client.PostAsJsonAsync("/api/provider/schedule/blocked-times", new { startsAt = "2030-01-02T09:00:00Z", endsAt = "2030-01-02T10:00:00Z" });
        var service = await client.PostAsJsonAsync("/api/provider/schedule/services", new { name = "Follow-up", active = true });
        var schedule = await client.GetFromJsonAsync<ProviderSchedule>("/api/provider/schedule");

        Assert.Equal(HttpStatusCode.OK, hours.StatusCode);
        Assert.Equal(HttpStatusCode.OK, length.StatusCode);
        Assert.Equal(HttpStatusCode.Created, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.Created, service.StatusCode);
        Assert.Equal(30, schedule!.SlotLengthMinutes);
        Assert.Equal([1, 2, 3, 4, 5], schedule.WorkingHours.Select(rule => rule.Weekday));
        Assert.Single(schedule.BlockedTimes);
        Assert.Contains(schedule.Services, item => item.Name == "Follow-up" && item.Active);
        Assert.All(factory.Repository.MutatingUsers, id => Assert.Equal(ScheduleApplicationFactory.UserId, id));
    }

    [Fact]
    public async Task RejectsPastBlockedTimeAndInvalidRulesBeforeMutation()
    {
        await using var factory = new ScheduleApplicationFactory(AppRole.Provider);
        using var client = AuthorizedClient(factory);
        var past = await client.PostAsJsonAsync("/api/provider/schedule/blocked-times", new { startsAt = "2029-12-31T23:00:00Z", endsAt = "2030-01-01T01:00:00Z" });
        var invalid = await client.PutAsJsonAsync("/api/provider/schedule/working-hours", new { rules = new[] { new { weekday = 1, localStart = "17:00", localEnd = "09:00" } } });

        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(factory.Repository.MutatingUsers);
    }

    [Fact]
    public async Task PatientCannotReadOrMutateProviderSettings()
    {
        await using var factory = new ScheduleApplicationFactory(AppRole.Patient);
        using var client = AuthorizedClient(factory);
        var read = await client.GetAsync("/api/provider/schedule");
        var write = await client.PutAsJsonAsync("/api/provider/schedule/slot-length", new { slotLengthMinutes = 30 });
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Empty(factory.Repository.MutatingUsers);
    }

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> factory) { var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "valid"); return client; }

    private sealed class ScheduleApplicationFactory(AppRole role) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public FakeSchedules Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IProviderScheduleRepository>(); services.RemoveAll<IClock>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier()); services.AddSingleton<IUserProfileRoleRepository>(new FakeRoles(role)); services.AddSingleton<IProviderScheduleRepository>(Repository); services.AddSingleton<IClock>(new FakeClock(Instant.FromUtc(2030, 1, 1, 0, 0)));
        });
    }
    private sealed class FakeVerifier : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(ScheduleApplicationFactory.UserId, true) : null); }
    private sealed class FakeRoles(AppRole role) : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(role); }
    private sealed class FakeSchedules : IProviderScheduleRepository
    {
        private readonly List<WorkingHours> rules = []; private readonly List<BlockedTime> blocks = []; private readonly List<OfferedService> services = []; public List<Guid> MutatingUsers { get; } = []; private int slotLength = 15;
        public Task<ProviderSchedule?> GetAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<ProviderSchedule?>(new(slotLength, rules, blocks, services));
        public Task<ProviderSchedule?> ReplaceWorkingHoursAsync(Guid userId, IReadOnlyList<WorkingHoursInput> input, DateOnly today, CancellationToken cancellationToken) { MutatingUsers.Add(userId); rules.Clear(); rules.AddRange(input.Select(rule => new WorkingHours(Guid.NewGuid(), rule.Weekday, rule.LocalStart, rule.LocalEnd, rule.EffectiveFrom ?? today, rule.EffectiveUntil))); return GetAsync(userId, cancellationToken); }
        public Task<ProviderSchedule?> UpdateSlotLengthAsync(Guid userId, int value, CancellationToken cancellationToken) { MutatingUsers.Add(userId); slotLength = value; return GetAsync(userId, cancellationToken); }
        public Task<BlockedTime?> CreateBlockedTimeAsync(Guid userId, Instant startsAt, Instant endsAt, CancellationToken cancellationToken) { MutatingUsers.Add(userId); var block = new BlockedTime(Guid.NewGuid(), startsAt, endsAt); blocks.Add(block); return Task.FromResult<BlockedTime?>(block); }
        public Task<BlockedTime?> UpdateBlockedTimeAsync(Guid userId, Guid blockedTimeId, Instant startsAt, Instant endsAt, CancellationToken cancellationToken) => Task.FromResult<BlockedTime?>(null);
        public Task<bool?> DeleteBlockedTimeAsync(Guid userId, Guid blockedTimeId, CancellationToken cancellationToken) => Task.FromResult<bool?>(false);
        public Task<OfferedService?> CreateServiceAsync(Guid userId, string name, bool active, CancellationToken cancellationToken) { MutatingUsers.Add(userId); var service = new OfferedService(Guid.NewGuid(), name, active); services.Add(service); return Task.FromResult<OfferedService?>(service); }
        public Task<OfferedService?> UpdateServiceAsync(Guid userId, Guid serviceId, string name, bool active, CancellationToken cancellationToken) => Task.FromResult<OfferedService?>(null);
        public Task<bool?> DeleteServiceAsync(Guid userId, Guid serviceId, CancellationToken cancellationToken) => Task.FromResult<bool?>(false);
    }
}
