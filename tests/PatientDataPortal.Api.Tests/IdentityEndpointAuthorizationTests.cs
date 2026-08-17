using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class IdentityEndpointAuthorizationTests
{
    [Fact]
    public async Task UnconfirmedEmailCannotClaim()
    {
        await using var factory = new IdentityApplicationFactory(false, false); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.PostAsJsonAsync("/api/identity/verify", new { patientRef = "PTDP-1", dob = "1980-01-02" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(factory.Verifier.LastEmailVerified);
    }

    [Fact]
    public async Task IdentityFailuresReturnOneGenericBody()
    {
        await using var factory = new IdentityApplicationFactory(true, false); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/identity/verify", new { patientRef = "unknown", dob = "1980-01-02" }),
            client.PostAsJsonAsync("/api/identity/verify", new { patientRef = "PTDP-1", dob = "1999-01-01" }),
            client.PostAsJsonAsync("/api/identity/verify", new { patientRef = "unknown", dob = "1999-01-01" }));

        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        Assert.Contains(IdentityVerificationService.GenericErrorCode, bodies[0]);
    }

    [Fact]
    public async Task UnverifiedPatientsAreForbiddenFromEveryProtectedResourceRoot()
    {
        await using var factory = new IdentityApplicationFactory(true, false); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        foreach (var path in new[] { "/api/studies", "/api/images", "/api/cine", "/api/reports" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }

    private sealed class IdentityApplicationFactory(bool emailVerified, bool verifiedPatient) : WebApplicationFactory<Program>
    {
        public FakeIdentityService Verifier { get; } = new(verifiedPatient);
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IIdentityVerificationService>(); services.RemoveAll<IAuditWriter>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeJwtVerifier(emailVerified)); services.AddSingleton<IUserProfileRoleRepository>(new PatientProfile()); services.AddSingleton<IIdentityVerificationService>(Verifier); services.AddSingleton<IAuditWriter>(new NoopAuditWriter());
        });
    }
    private sealed class FakeJwtVerifier(bool emailVerified) : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957"), emailVerified) : null); }
    private sealed class PatientProfile : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient); }
    public sealed class NoopAuditWriter : IAuditWriter { public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) => Task.CompletedTask; }
    public sealed class FakeIdentityService(bool verifiedPatient) : IIdentityVerificationService
    {
        public bool LastEmailVerified { get; private set; }
        public Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken) { LastEmailVerified = emailVerified; return Task.FromResult(new IdentityVerificationResult(false, Duration.Zero)); }
        public Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(verifiedPatient);
        public Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
