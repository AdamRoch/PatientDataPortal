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
        await using var factory = new ReportsApplicationFactory(verified: true, [ownSigned], null);
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
        await using var factory = new ReportsApplicationFactory(verified: true, [], new SignedReportStorageItem(id, "reports/signed.pdf"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync($"/api/reports/{id}/view");
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://storage.example.test/signed.pdf", body!["url"]);
        Assert.Equal("reports/signed.pdf", factory.Storage.LastStoragePath);
        var audit = Assert.Single(factory.Audit.AllowedEvents);
        Assert.Equal("report_view", audit.Action);
        Assert.Equal("report", audit.TargetType);
        Assert.Equal(id.ToString(), audit.TargetReference);
    }

    [Fact]
    public async Task PreliminaryOrOtherPatientReportCannotBeViewed()
    {
        await using var factory = new ReportsApplicationFactory(verified: true, [], null);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync($"/api/reports/{Guid.NewGuid()}/view");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.Storage.LastStoragePath);
        Assert.Empty(factory.Audit.AllowedEvents);
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

    private sealed class ReportsApplicationFactory(bool verified, IReadOnlyList<SignedReportListItem> list, SignedReportStorageItem? report) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public FakeReports Reports { get; } = new(list, report);
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

    private sealed class FakeJwtVerifier : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(ReportsApplicationFactory.UserId, true) : null); }
    private sealed class PatientRole : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient); }
    private sealed class FakeIdentityService(bool verified) : IIdentityVerificationService
    {
        public Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(verified);
        public Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class FakeReports(IReadOnlyList<SignedReportListItem> list, SignedReportStorageItem? report) : IReportRepository
    {
        public Guid? ListAccountId { get; private set; }
        public Task<IReadOnlyList<SignedReportListItem>> ListSignedForPatientAsync(Guid accountId, CancellationToken cancellationToken) { ListAccountId = accountId; return Task.FromResult(list); }
        public Task<SignedReportStorageItem?> FindSignedForPatientAsync(Guid reportId, Guid accountId, CancellationToken cancellationToken) => Task.FromResult(report?.Id == reportId ? report : null);
    }
    private sealed class FakeStorage : IReportStorage
    {
        public string? LastStoragePath { get; private set; }
        public Task<Uri> CreateSignedReadUrlAsync(string storagePath, CancellationToken cancellationToken) { LastStoragePath = storagePath; return Task.FromResult(new Uri("https://storage.example.test/signed.pdf")); }
    }
    private sealed class CapturingAudit : IAuditWriter
    {
        public List<AuditEvent> AllowedEvents { get; } = [];
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;
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
                Content = JsonContent.Create(new { signedURL = "/storage/v1/object/sign/reports/signed.pdf?token=short-lived" }),
            };
        }
    }
}
