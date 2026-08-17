using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Audit;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class AuditLogEndpointTests
{
    [Fact]
    public async Task AdminGetsAllEntriesWithServerSideFilters()
    {
        await using var factory = new AuditLogApplicationFactory(AppRole.Admin);
        using var client = AuthorizedClient(factory);

        var response = await client.GetAsync("/api/audit-log?actor=actor-ref&action=report_list_viewed&date=2026-08-16");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new AuditLogFilters("actor-ref", "report_list_viewed", new DateOnly(2026, 8, 16)), factory.Log.AdminFilters);
        Assert.Null(factory.Log.ProviderRequest);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(["action", "actorReference", "actorRole", "occurredAt", "result", "targetReference", "targetType"], document.RootElement[0].EnumerateObject().Select(property => property.Name).Order());
        Assert.DoesNotContain("patientName", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderQueryIsAlwaysBoundToAuthenticatedProvider()
    {
        await using var factory = new AuditLogApplicationFactory(AppRole.Provider);
        using var client = AuthorizedClient(factory);

        var response = await client.GetAsync("/api/audit-log?actor=another-user&action=content_access_granted");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(factory.Log.AdminFilters);
        Assert.Equal((AuditLogApplicationFactory.UserId, new AuditLogFilters("another-user", "content_access_granted", null)), factory.Log.ProviderRequest);
        Assert.Contains(factory.Audit.Events, entry => entry.Action == "audit_log_viewed" && entry.TargetReference == "own_provider_patients");
    }

    [Fact]
    public async Task PatientCannotReadAuditLog()
    {
        await using var factory = new AuditLogApplicationFactory(AppRole.Patient);
        using var client = AuthorizedClient(factory);

        var response = await client.GetAsync("/api/audit-log");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(factory.Log.AdminFilters);
        Assert.Null(factory.Log.ProviderRequest);
    }

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        return client;
    }

    private sealed class AuditLogApplicationFactory(AppRole role) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("ce8d7726-7449-4b3a-aa5d-3d37a4c94f4b");
        public FakeAuditLog Log { get; } = new();
        public CapturingAudit Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IAuditLogRepository>(); services.RemoveAll<IAuditWriter>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier()); services.AddSingleton<IUserProfileRoleRepository>(new FakeProfiles(role)); services.AddSingleton<IAuditLogRepository>(Log); services.AddSingleton<IAuditWriter>(Audit);
        });
    }

    private sealed class FakeVerifier : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(AuditLogApplicationFactory.UserId, true) : null); }
    private sealed class FakeProfiles(AppRole role) : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(role); }
    private sealed class FakeAuditLog : IAuditLogRepository
    {
        public AuditLogFilters? AdminFilters { get; private set; }
        public (Guid UserId, AuditLogFilters Filters)? ProviderRequest { get; private set; }
        public Task<IReadOnlyList<AuditLogItem>> ListForAdminAsync(AuditLogFilters filters, CancellationToken cancellationToken) { AdminFilters = filters; return Task.FromResult<IReadOnlyList<AuditLogItem>>([Row()]); }
        public Task<IReadOnlyList<AuditLogItem>> ListForProviderAsync(Guid providerUserId, AuditLogFilters filters, CancellationToken cancellationToken) { ProviderRequest = (providerUserId, filters); return Task.FromResult<IReadOnlyList<AuditLogItem>>([Row()]); }
        private static AuditLogItem Row() => new("actor-ref", "provider", "report_list_viewed", "report", "867c7d1e-b900-4a90-aa3e-147b50a1c21c", "allowed", DateTimeOffset.Parse("2026-08-16T10:00:00Z"));
    }
    private sealed class CapturingAudit : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteAllowedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
    }
}
