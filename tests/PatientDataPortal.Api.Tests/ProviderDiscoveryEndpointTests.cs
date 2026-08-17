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

public sealed class ProviderDiscoveryEndpointTests
{
    [Fact]
    public async Task PatientCanDiscoverProvidersAndOnlyFutureOpenSlotsInTheRequestedRange()
    {
        await using var factory = new DiscoveryApplicationFactory(AppRole.Patient);
        using var client = AuthorizedClient(factory);
        var providers = await client.GetFromJsonAsync<List<DiscoverableProvider>>("/api/providers");
        var slots = await client.GetFromJsonAsync<List<OpenSlot>>($"/api/providers/{DiscoveryApplicationFactory.ProviderId}/slots?from=2029-12-31T00%3A00%3A00Z&to=2030-01-03T00%3A00%3A00Z");

        Assert.NotNull(providers);
        Assert.Single(providers);
        Assert.Equal("Dr. Rivera", providers[0].Name);
        Assert.Single(providers[0].Services);
        Assert.NotNull(slots);
        Assert.Single(slots);
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 9, 0, 0, TimeSpan.Zero), slots[0].StartsAt);
        Assert.Equal(Instant.FromUtc(2029, 12, 31, 0, 0), factory.Repository.LastFrom);
        Assert.Equal(Instant.FromUtc(2030, 1, 3, 0, 0), factory.Repository.LastTo);
    }

    [Fact]
    public async Task RejectsMissingOrOverlongSlotRangesAndNonPatients()
    {
        await using var patientFactory = new DiscoveryApplicationFactory(AppRole.Patient);
        using var patient = AuthorizedClient(patientFactory);
        var missing = await patient.GetAsync($"/api/providers/{DiscoveryApplicationFactory.ProviderId}/slots");
        var longRange = await patient.GetAsync($"/api/providers/{DiscoveryApplicationFactory.ProviderId}/slots?from=2030-01-01T00%3A00%3A00Z&to=2030-02-02T00%3A00%3A00Z");
        await using var providerFactory = new DiscoveryApplicationFactory(AppRole.Provider);
        using var provider = AuthorizedClient(providerFactory);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longRange.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await provider.GetAsync("/api/providers")).StatusCode);
        Assert.Null(patientFactory.Repository.LastFrom);
    }

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> factory) { var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "valid"); return client; }

    private sealed class DiscoveryApplicationFactory(AppRole role) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("a8c88a39-e945-4da5-a5b7-6a3c901a1d1b");
        public static readonly Guid ProviderId = Guid.Parse("425d78d4-6f3c-4867-ac79-fdf3ddca5a54");
        public FakeDiscovery Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IProviderDiscoveryRepository>(); services.RemoveAll<IClock>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier()); services.AddSingleton<IUserProfileRoleRepository>(new FakeRoles(role)); services.AddSingleton<IProviderDiscoveryRepository>(Repository); services.AddSingleton<IClock>(new FakeClock(Instant.FromUtc(2030, 1, 1, 0, 0)));
        });
    }
    private sealed class FakeVerifier : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(DiscoveryApplicationFactory.UserId, true) : null); }
    private sealed class FakeRoles(AppRole role) : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(role); }
    private sealed class FakeDiscovery : IProviderDiscoveryRepository
    {
        public Instant? LastFrom { get; private set; }
        public Instant? LastTo { get; private set; }
        public Task<IReadOnlyList<DiscoverableProvider>> ListProvidersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DiscoverableProvider>>([new(DiscoveryApplicationFactory.ProviderId, "Dr. Rivera", [new(Guid.Parse("90b8bf63-34ca-4137-9f98-1e96fc16b44f"), "Consultation", true)])]);
        public Task<IReadOnlyList<OpenSlot>> ListOpenSlotsAsync(Guid providerId, Instant from, Instant to, CancellationToken cancellationToken)
        {
            LastFrom = from; LastTo = to;
            return Task.FromResult<IReadOnlyList<OpenSlot>>([new(Guid.Parse("b6060d96-d4d1-45f8-a29e-c153aa1e92e6"), new DateTimeOffset(2030, 1, 2, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 2, 9, 30, 0, TimeSpan.Zero))]);
        }
    }
}
