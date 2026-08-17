using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Security;
using PatientDataPortal.Api.Studies;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class StudiesEndpointTests
{
    [Theory]
    [InlineData("052e7848-3763-40f7-b45a-7c8c38320788")]
    [InlineData("cf4f0cb1-0cc1-4b6c-b287-4cd4f65df1be")]
    public async Task PatientRecordIdGuessingCannotExposeAnotherPatientsStudies(string guessedPatientRecordId)
    {
        var ownStudy = new StudyListItem(Guid.Parse("f31380f3-d3e6-499c-aed5-c0e997bb2919"), DateTimeOffset.Parse("2026-01-10T12:00:00Z"), "Follow-up ultrasound");
        await using var factory = new StudiesApplicationFactory(verified: true, new Dictionary<Guid, IReadOnlyList<StudyListItem>>
        {
            [StudiesApplicationFactory.UserId] = [ownStudy],
            [StudiesApplicationFactory.OtherUserId] =
            [new(Guid.Parse("b5396867-2796-4088-a78a-cac77b10d177"), DateTimeOffset.Parse("2026-01-11T12:00:00Z"), "Another patient's study")],
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync("/api/studies?patientRecordId=" + guessedPatientRecordId);
        var studies = await response.Content.ReadFromJsonAsync<List<StudyListItem>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([ownStudy], studies);
        Assert.DoesNotContain(studies!, study => study.Description == "Another patient's study");
        Assert.Equal(StudiesApplicationFactory.UserId, factory.Studies.LastAccountId);
    }

    [Fact]
    public async Task UnverifiedPatientIsDeniedAndAuditedBeforeStudiesAreRead()
    {
        await using var factory = new StudiesApplicationFactory(verified: false, new Dictionary<Guid, IReadOnlyList<StudyListItem>>());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync("/api/studies");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Studies.AccountIds);
        var audit = Assert.Single(factory.Audit.Events);
        Assert.Equal("verified_patient_required", audit.Action);
        Assert.Equal("api_route", audit.TargetType);
        Assert.Equal("/api/studies", audit.TargetReference);
        Assert.Equal("denied", audit.Result);
    }

    private sealed class StudiesApplicationFactory(bool verified, IReadOnlyDictionary<Guid, IReadOnlyList<StudyListItem>> studies) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public static readonly Guid OtherUserId = Guid.Parse("052e7848-3763-40f7-b45a-7c8c38320788");
        public FakeStudies Studies { get; } = new(studies);
        public CapturingAuditWriter Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>();
            services.RemoveAll<IUserProfileRoleRepository>();
            services.RemoveAll<IIdentityVerificationService>();
            services.RemoveAll<IAuditWriter>();
            services.RemoveAll<IStudyRepository>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeJwtVerifier());
            services.AddSingleton<IUserProfileRoleRepository>(new PatientRole());
            services.AddSingleton<IIdentityVerificationService>(new FakeIdentityService(verified));
            services.AddSingleton<IAuditWriter>(Audit);
            services.AddSingleton<IStudyRepository>(Studies);
        });
    }

    private sealed class FakeJwtVerifier : ISupabaseJwtVerifier
    {
        public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(token == "valid" ? new AuthenticatedUser(StudiesApplicationFactory.UserId, true) : null);
    }

    private sealed class PatientRole : IUserProfileRoleRepository
    {
        public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient);
    }

    private sealed class FakeIdentityService(bool verified) : IIdentityVerificationService
    {
        public Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(verified);
        public Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public sealed class FakeStudies(IReadOnlyDictionary<Guid, IReadOnlyList<StudyListItem>> studies) : IStudyRepository
    {
        public List<Guid> AccountIds { get; } = [];
        public Guid? LastAccountId => AccountIds.LastOrDefault();
        public Task<IReadOnlyList<StudyListItem>> ListCompletedForPatientAsync(Guid accountId, CancellationToken cancellationToken)
        {
            AccountIds.Add(accountId);
            return Task.FromResult(studies.GetValueOrDefault(accountId, []));
        }
    }

    public sealed class CapturingAuditWriter : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
    }
}
