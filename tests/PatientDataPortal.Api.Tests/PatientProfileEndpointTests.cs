using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Profiles;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class PatientProfileEndpointTests
{
    [Fact]
    public async Task PatientCanReadAndUpdateOnlyTheirOwnProfile()
    {
        await using var factory = new ProfileApplicationFactory(AppRole.Patient);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var before = await client.GetFromJsonAsync<PatientProfile>("/api/profile");
        var response = await client.PutAsJsonAsync("/api/profile", new
        {
            displayName = "Ada Patient",
            timeZone = "America/Chicago",
            userId = ProfileApplicationFactory.OtherUserId,
        });
        var after = await client.GetFromJsonAsync<PatientProfile>("/api/profile");

        Assert.Equal("Patient One", before!.DisplayName);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ada Patient", after!.DisplayName);
        Assert.Equal("America/Chicago", after.TimeZone);
        Assert.Equal(ProfileApplicationFactory.UserId, factory.Profiles.LastUpdatedUserId);
        Assert.Equal("Other Patient", factory.Profiles.GetStored(ProfileApplicationFactory.OtherUserId).DisplayName);
    }

    [Fact]
    public async Task ProfileRejectsUnauthenticatedWrongRoleAndInvalidUpdates()
    {
        await using var patientFactory = new ProfileApplicationFactory(AppRole.Patient);
        using var unauthenticated = patientFactory.CreateClient();
        var unauthenticatedResponse = await unauthenticated.GetAsync("/api/profile");

        using var patient = patientFactory.CreateClient();
        patient.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        var invalidResponse = await patient.PutAsJsonAsync("/api/profile", new { displayName = " ", timeZone = "Not/AZone" });

        await using var providerFactory = new ProfileApplicationFactory(AppRole.Provider);
        using var provider = providerFactory.CreateClient();
        provider.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        var forbiddenResponse = await provider.GetAsync("/api/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
    }

    private sealed class ProfileApplicationFactory(AppRole role) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public static readonly Guid OtherUserId = Guid.Parse("052e7848-3763-40f7-b45a-7c8c38320788");
        public FakeProfiles Profiles { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISupabaseJwtVerifier>();
                services.RemoveAll<IUserProfileRoleRepository>();
                services.RemoveAll<IPatientProfileRepository>();
                services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier());
                services.AddSingleton<IUserProfileRoleRepository>(new FakeRoles(role));
                services.AddSingleton<IPatientProfileRepository>(Profiles);
            });
        }
    }

    private sealed class FakeVerifier : ISupabaseJwtVerifier
    {
        public Task<Guid?> VerifyAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(token == "valid" ? ProfileApplicationFactory.UserId : (Guid?)null);
    }

    private sealed class FakeRoles(AppRole role) : IUserProfileRoleRepository
    {
        public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(role);
    }

    public sealed class FakeProfiles : IPatientProfileRepository
    {
        private readonly Dictionary<Guid, PatientProfile> profiles = new()
        {
            [ProfileApplicationFactory.UserId] = new("Patient One", "UTC"),
            [ProfileApplicationFactory.OtherUserId] = new("Other Patient", "UTC"),
        };

        public Guid? LastUpdatedUserId { get; private set; }

        public Task<PatientProfile?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(profiles.GetValueOrDefault(userId));

        public Task<PatientProfile?> UpdateAsync(Guid userId, string displayName, string timeZone, CancellationToken cancellationToken)
        {
            LastUpdatedUserId = userId;
            var profile = new PatientProfile(displayName, timeZone);
            profiles[userId] = profile;
            return Task.FromResult<PatientProfile?>(profile);
        }

        public PatientProfile GetStored(Guid userId) => profiles[userId];
    }
}
