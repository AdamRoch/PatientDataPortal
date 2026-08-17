using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Email;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class EmailOutboxStatusEndpointTests
{
    [Fact]
    public async Task AdminCanReadOnlySafeEmailOutboxStatusFields()
    {
        var row = new EmailOutboxStatusItem("share", "pending", 1, DateTimeOffset.Parse("2026-08-16T12:00:00Z"), null, null);
        await using var factory = new OutboxStatusApplicationFactory(AppRole.Admin, [row]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync("/api/admin/email-outbox");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["attempts", "dueAt", "kind", "providerMessageId", "sentAt", "status"], document.RootElement[0].EnumerateObject().Select(property => property.Name).Order());
        Assert.DoesNotContain("payload", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://portal.example.test/share/a-secret-token", body, StringComparison.Ordinal);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "email_outbox_status_view" && audit.Result == "allowed");
    }

    [Fact]
    public async Task NonAdminCannotReadEmailOutboxStatus()
    {
        await using var factory = new OutboxStatusApplicationFactory(AppRole.Provider, []);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync("/api/admin/email-outbox");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Outbox.Requests);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "authorization_denied" && audit.Result == "denied");
    }

    private sealed class OutboxStatusApplicationFactory(AppRole role, IReadOnlyList<EmailOutboxStatusItem> rows) : WebApplicationFactory<Program>
    {
        public FakeOutbox Outbox { get; } = new(rows);
        public CollectingAudit Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>();
            services.RemoveAll<IUserProfileRoleRepository>();
            services.RemoveAll<IEmailOutboxStatusRepository>();
            services.RemoveAll<IAuditWriter>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier());
            services.AddSingleton<IUserProfileRoleRepository>(new FakeProfiles(role));
            services.AddSingleton<IEmailOutboxStatusRepository>(Outbox);
            services.AddSingleton<IAuditWriter>(Audit);
        });
    }

    private sealed class FakeVerifier : ISupabaseJwtVerifier
    {
        public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(token == "valid" ? new AuthenticatedUser(Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957"), true) : null);
    }

    private sealed class FakeProfiles(AppRole role) : IUserProfileRoleRepository
    {
        public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(role);
    }

    public sealed class FakeOutbox(IReadOnlyList<EmailOutboxStatusItem> rows) : IEmailOutboxStatusRepository
    {
        public List<string> Requests { get; } = [];
        public Task<IReadOnlyList<EmailOutboxStatusItem>> ListAsync(CancellationToken cancellationToken)
        {
            Requests.Add("list");
            return Task.FromResult(rows);
        }
    }

    public sealed class CollectingAudit : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteAllowedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
    }
}
