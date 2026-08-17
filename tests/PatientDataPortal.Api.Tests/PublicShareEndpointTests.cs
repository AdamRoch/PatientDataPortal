using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Security;
using PatientDataPortal.Api.Sharing;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class PublicShareEndpointTests
{
    [Fact]
    public async Task RevokedTokenServesUnavailableExperienceWithoutContentAndAuditsDenials()
    {
        await using var factory = new PublicShareApplicationFactory();
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/api/public/share/live/content");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl!.Private);
        Assert.True(first.Headers.CacheControl.NoStore);
        Assert.Equal("file bytes", await first.Content.ReadAsStringAsync());

        factory.Shares.IsActive = false;
        var reloadedPage = await client.GetAsync("/api/public/share/live");
        var backNavigationContent = await client.GetAsync("/api/public/share/live/content");

        Assert.Equal(HttpStatusCode.NotFound, reloadedPage.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, backNavigationContent.StatusCode);
        Assert.DoesNotContain("file bytes", await backNavigationContent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(3, factory.Audit.Events.Count);
        Assert.All(factory.Audit.Events.Skip(1), audit => AssertDenied(audit));
    }

    [Fact]
    public async Task ExpiredTokenServesUnavailableExperienceWithoutContentAndAuditsDenial()
    {
        await using var factory = new PublicShareApplicationFactory { Shares = { IsActive = false } };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/share/expired/content");

        await AssertUnavailableAsync(response, factory.Audit);
    }

    [Theory]
    [InlineData("foreign-valid-looking-token")]
    [InlineData("not-a-token!!!")]
    public async Task ForeignOrMalformedTokenServesUnavailableExperienceWithoutContentAndAuditsDenial(string token)
    {
        await using var factory = new PublicShareApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/share/{Uri.EscapeDataString(token)}/content");

        await AssertUnavailableAsync(response, factory.Audit);
    }

    [Fact]
    public async Task CacheRevalidationAfterExpiryNeverReturnsPreviouslyDeliveredContent()
    {
        await using var factory = new PublicShareApplicationFactory();
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/api/public/share/live/content");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl!.NoStore);

        factory.Shares.IsActive = false;
        var revalidated = await client.GetAsync("/api/public/share/live/content", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.NotFound, revalidated.StatusCode);
        Assert.DoesNotContain("file bytes", await revalidated.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(revalidated.Headers.CacheControl!.NoStore);
        Assert.Equal(2, factory.Audit.Events.Count);
        AssertDenied(factory.Audit.Events[1]);
        Assert.Equal(1, factory.Storage.OpenCount);
    }

    [Fact]
    public async Task ContentStreamsBytesWithPrivacyHeadersAndNoStorageUrl()
    {
        await using var factory = new PublicShareApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/share/live/content?disposition=inline");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.StartsWith("inline;", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.Equal("file bytes", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("supabase", response.RequestMessage!.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(factory.Audit.Events);
        Assert.Equal("shared_content_delivered", factory.Audit.Events[0].Action);
    }

    [Fact]
    public async Task TokenIsNotReflectedInDeliveryHeadersOrAudit()
    {
        await using var factory = new PublicShareApplicationFactory();
        using var client = factory.CreateClient();
        const string token = "very-secret-share-token";
        factory.Shares.ActiveToken = token;

        var response = await client.GetAsync($"/api/public/share/{token}/content");
        var exposedValues = string.Join("\n", response.Headers.SelectMany(header => header.Value));

        Assert.DoesNotContain(token, exposedValues, StringComparison.Ordinal);
        Assert.DoesNotContain(token, factory.Audit.Events[0].TargetReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptedTokenGuessingIsRateLimited()
    {
        await using var factory = new PublicShareApplicationFactory { Shares = { IsActive = false } };
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 11).Select(index => client.GetAsync($"/api/public/share/guess-{index}")));

        Assert.All(responses, response => Assert.NotEqual(HttpStatusCode.OK, response.StatusCode));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(11, factory.Audit.Events.Count);
        Assert.All(factory.Audit.Events, AssertDenied);
        Assert.Equal(0, factory.Storage.OpenCount);
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response, FakeAudit audit)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("file bytes", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        var denial = Assert.Single(audit.Events);
        AssertDenied(denial);
    }

    private static void AssertDenied(AuditEvent audit)
    {
        Assert.Equal("shared_content_denied", audit.Action);
        Assert.Equal("share_link", audit.TargetType);
        Assert.Equal("denied", audit.Result);
        Assert.DoesNotContain("token", audit.TargetReference, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PublicShareApplicationFactory : WebApplicationFactory<Program>
    {
        public FakePublicShares Shares { get; } = new();
        public FakeStorage Storage { get; } = new();
        public FakeAudit Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPublicShareService>(); services.RemoveAll<IPublicShareStorage>(); services.RemoveAll<IPublicShareFailureLimiter>(); services.RemoveAll<IAuditWriter>();
            services.AddSingleton<IPublicShareService>(Shares); services.AddSingleton<IPublicShareStorage>(Storage); services.AddSingleton<IPublicShareFailureLimiter, PublicShareFailureLimiter>(); services.AddSingleton<IAuditWriter>(Audit);
        });
    }

    private sealed class FakePublicShares : IPublicShareService
    {
        public bool IsActive { get; set; } = true;
        public string ActiveToken { get; set; } = "live";
        public Task<PublicShare?> FindActiveAsync(string token, CancellationToken cancellationToken) => Task.FromResult<PublicShare?>(IsActive && token == ActiveToken ? new PublicShare(Guid.Parse("b8a1bf14-359e-4b52-9eb4-66e1cc099c71"), "report", "reports/private.pdf") : null);
    }

    private sealed class FakeStorage : IPublicShareStorage
    {
        public int OpenCount { get; private set; }
        public Task<PublicShareContent?> OpenReadAsync(PublicShare share, CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult<PublicShareContent?>(new PublicShareContent(new MemoryStream(Encoding.UTF8.GetBytes("file bytes")), "application/pdf", "shared-file.pdf"));
        }
    }

    private sealed class FakeAudit : IAuditWriter
    {
        private readonly object gate = new();
        private readonly List<AuditEvent> events = [];
        public IReadOnlyList<AuditEvent> Events { get { lock (gate) return events.ToArray(); } }
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) => WriteAsync(auditEvent);
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) => WriteAsync(auditEvent);
        public Task WriteAllowedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) => WriteAsync(auditEvent);
        private Task WriteAsync(AuditEvent auditEvent) { lock (gate) events.Add(auditEvent); return Task.CompletedTask; }
    }
}
