using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Reports;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class ReportsEndpointTests
{
    [Fact]
    public async Task VerifiedPatientListsOnlyOwnSignedReports()
    {
        var ownSigned = new SignedReportListItem(Guid.Parse("f31380f3-d3e6-499c-aed5-c0e997bb2919"), DateTimeOffset.Parse("2026-01-10T12:00:00Z"), "Follow-up ultrasound");
        await using var factory = new ReportsApplicationFactory(verified: true, [ownSigned], []);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync("/api/reports?patientRecordId=other-patient");
        var reports = await response.Content.ReadFromJsonAsync<List<SignedReportListItem>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([ownSigned], reports);
        Assert.Equal(ReportsApplicationFactory.UserId, factory.Reports.ListAccountId);
        Assert.DoesNotContain("preliminary", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedReportViewIssuesShortLivedUrlAndAuditsTheView()
    {
        var id = Guid.Parse("f31380f3-d3e6-499c-aed5-c0e997bb2919");
        await using var factory = new ReportsApplicationFactory(verified: true, [], [new ReportFixture(id, ReportsApplicationFactory.UserId, "reports/signed.pdf", Signed: true)]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync($"/api/reports/{id}/view");
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://storage.example.test/signed.pdf", body!["url"]);
        Assert.Equal("reports/signed.pdf", factory.Storage.LastStoragePath);
        var audit = Assert.Single(factory.Audit.AllowedEvents);
        Assert.Equal("content_access_granted", audit.Action);
        Assert.Equal("report", audit.TargetType);
        Assert.Equal(id.ToString(), audit.TargetReference);
    }

    [Fact]
    public async Task ForeignPatientReportIsHiddenWithoutIssuingReportBytesAndIsAudited()
    {
        var report = new ReportFixture(Guid.NewGuid(), ReportsApplicationFactory.UserId, "reports/signed.pdf", Signed: true);
        await using var factory = new ReportsApplicationFactory(verified: true, [], [report]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "foreign");

        var response = await client.GetAsync($"/api/reports/{report.Id}/view");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoReportBytes(response, factory);
        AssertDeniedReportView(factory, report.Id, ReportsApplicationFactory.ForeignUserId);
    }

    [Fact]
    public async Task PreliminaryReportIsHiddenWithoutIssuingReportBytesAndIsAudited()
    {
        var preliminary = new ReportFixture(Guid.NewGuid(), ReportsApplicationFactory.UserId, "reports/preliminary.pdf", Signed: false);
        await using var factory = new ReportsApplicationFactory(verified: true, [], [preliminary]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync($"/api/reports/{preliminary.Id}/view");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoReportBytes(response, factory);
        AssertDeniedReportView(factory, preliminary.Id, ReportsApplicationFactory.UserId);
    }

    [Fact]
    public async Task TamperedJwtCannotProbeReportsAndIsAudited()
    {
        var report = new ReportFixture(Guid.NewGuid(), ReportsApplicationFactory.UserId, "reports/signed.pdf", Signed: true);
        await using var factory = new ReportsApplicationFactory(verified: true, [], [report]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "tampered");

        var response = await client.GetAsync($"/api/reports/{report.Id}/view");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoReportBytes(response, factory);
        Assert.Contains(factory.Audit.DeniedEvents, audit => audit.Action == "authentication_denied" && audit.Result == "denied");
    }

    [Fact]
    public async Task UnverifiedPatientCannotProbeReportsAndIsAudited()
    {
        var report = new ReportFixture(Guid.NewGuid(), ReportsApplicationFactory.UserId, "reports/signed.pdf", Signed: true);
        await using var factory = new ReportsApplicationFactory(verified: false, [], [report]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync($"/api/reports/{report.Id}/view");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoReportBytes(response, factory);
        Assert.Contains(factory.Audit.DeniedEvents, audit => audit.Action == "verified_patient_required" && audit.Result == "denied");
    }

    [Fact]
    public async Task PrivateStorageUrlExpiresInOneMinute()
    {
        var handler = new CapturingStorageHandler();
        var storage = new SupabaseReportStorage(new TestHttpClientFactory(handler), Options.Create(new SupabaseOptions
        {
            Url = "https://storage.example.test",
            ServiceKey = "service-key",
        }));

        var url = await storage.CreateSignedReadUrlAsync("reports/signed.pdf", CancellationToken.None);

        Assert.Equal("https://storage.example.test/storage/v1/object/sign/reports/signed.pdf?token=short-lived", url.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("service-key", handler.AuthorizationParameter);
        Assert.Contains("\"expiresIn\":60", handler.Body);
    }

    private static async Task AssertNoReportBytes(HttpResponseMessage response, ReportsApplicationFactory factory)
    {
        Assert.Null(factory.Storage.LastStoragePath);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("reports/", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage.example.test", body, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertDeniedReportView(ReportsApplicationFactory factory, Guid reportId, Guid actorId) =>
        Assert.Contains(factory.Audit.DeniedEvents, audit => audit.ActorReference == actorId.ToString() && audit.Action == "content_access_denied" && audit.TargetType == "report" && audit.TargetReference == reportId.ToString() && audit.Result == "denied");

    private sealed record ReportFixture(Guid Id, Guid OwnerId, string StoragePath, bool Signed);

    private sealed class ReportsApplicationFactory(bool verified, IReadOnlyList<SignedReportListItem> list, IReadOnlyList<ReportFixture> reports) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public static readonly Guid ForeignUserId = Guid.Parse("c997fc3b-77a6-4a2a-841c-d6685d2a5ece");
        public FakeReports Reports { get; } = new(list, reports);
        public FakeStorage Storage { get; } = new();
        public CapturingAudit Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>();
            services.RemoveAll<IUserProfileRoleRepository>();
            services.RemoveAll<IIdentityVerificationService>();
            services.RemoveAll<IReportRepository>();
            services.RemoveAll<IReportStorage>();
            services.RemoveAll<IAuditWriter>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeJwtVerifier());
            services.AddSingleton<IUserProfileRoleRepository>(new PatientRole());
            services.AddSingleton<IIdentityVerificationService>(new FakeIdentityService(verified));
            services.AddSingleton<IReportRepository>(Reports);
            services.AddSingleton<IReportStorage>(Storage);
            services.AddSingleton<IAuditWriter>(Audit);
        });
    }

    private sealed class FakeJwtVerifier : ISupabaseJwtVerifier
    {
        public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token switch
        {
            "valid" => new AuthenticatedUser(ReportsApplicationFactory.UserId, true),
            "foreign" => new AuthenticatedUser(ReportsApplicationFactory.ForeignUserId, true),
            _ => null,
        });
    }
    private sealed class PatientRole : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient); }
    private sealed class FakeIdentityService(bool verified) : IIdentityVerificationService
    {
        public Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(verified);
        public Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class FakeReports(IReadOnlyList<SignedReportListItem> list, IReadOnlyList<ReportFixture> reports) : IReportRepository
    {
        public Guid? ListAccountId { get; private set; }
        public Task<IReadOnlyList<SignedReportListItem>> ListSignedForPatientAsync(Guid accountId, CancellationToken cancellationToken) { ListAccountId = accountId; return Task.FromResult(list); }
        public Task<SignedReportStorageItem?> FindSignedForPatientAsync(Guid reportId, Guid accountId, CancellationToken cancellationToken)
        {
            var report = reports.SingleOrDefault(report => report.Id == reportId && report.OwnerId == accountId && report.Signed);
            return Task.FromResult(report is null ? null : new SignedReportStorageItem(report.Id, report.StoragePath));
        }
    }
    private sealed class FakeStorage : IReportStorage
    {
        public string? LastStoragePath { get; private set; }
        public Task<Uri> CreateSignedReadUrlAsync(string storagePath, CancellationToken cancellationToken) { LastStoragePath = storagePath; return Task.FromResult(new Uri("https://storage.example.test/signed.pdf")); }
    }
    private sealed class CapturingAudit : IAuditWriter
    {
        public List<AuditEvent> AllowedEvents { get; } = [];
        public List<AuditEvent> DeniedEvents { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { AllowedEvents.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { DeniedEvents.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteAllowedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { AllowedEvents.Add(auditEvent); return Task.CompletedTask; }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingStorageHandler : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { signedURL = "/object/sign/reports/signed.pdf?token=short-lived" }),
            };
        }
    }
}
