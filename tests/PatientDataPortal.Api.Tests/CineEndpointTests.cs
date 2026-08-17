using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Cine;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class CineEndpointTests
{
    [Fact]
    public async Task OwnedManifestPreservesFrameOrderAndAuditsTheGrant()
    {
        var clipId = Guid.NewGuid();
        await using var factory = new CineApplicationFactory(clipId, OwnedClip(clipId));
        using var client = AuthorizedClient(factory);

        var response = await client.GetAsync($"/api/cine/{clipId}");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("studies/s/cine/c/f0001.jpg", payload.RootElement.GetProperty("manifest").GetProperty("frames")[0].GetProperty("path").GetString());
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "content_access_granted" && audit.Result == "allowed");
        Assert.Equal(CineApplicationFactory.UserId, factory.Repository.LastAccountId);
    }

    [Fact]
    public async Task FrameUrlsAreServerBoundedAndAudited()
    {
        var clipId = Guid.NewGuid();
        await using var factory = new CineApplicationFactory(clipId, OwnedClip(clipId));
        using var client = AuthorizedClient(factory);

        var response = await client.PostAsJsonAsync($"/api/cine/{clipId}/frame-urls", new { startFrame = 1, count = 2 });
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, payload.RootElement.GetProperty("frames").GetArrayLength());
        Assert.Equal(["studies/s/cine/c/f0002.jpg", "studies/s/cine/c/f0003.jpg"], factory.Signer.LastPaths);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "content_access_granted" && audit.Result == "allowed");
    }

    [Fact]
    public async Task OversizedFrameBatchIsRejectedBeforeAnyDataAccess()
    {
        var clipId = Guid.NewGuid();
        await using var factory = new CineApplicationFactory(clipId, OwnedClip(clipId));
        using var client = AuthorizedClient(factory);

        var response = await client.PostAsJsonAsync($"/api/cine/{clipId}/frame-urls", new { startFrame = 0, count = 51 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Repository.AccountIds);
        Assert.Empty(factory.Signer.LastPaths);
    }

    [Fact]
    public async Task ForeignClipIsHiddenAndDeniedAccessIsAudited()
    {
        var clipId = Guid.NewGuid();
        await using var factory = new CineApplicationFactory(clipId, null);
        using var client = AuthorizedClient(factory);

        var response = await client.GetAsync($"/api/cine/{clipId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Signer.LastPaths);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "content_access_denied" && audit.Result == "denied");
    }

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        return client;
    }

    private static CineClipAccess OwnedClip(Guid id)
    {
        using var document = JsonDocument.Parse("""{"frames":[{"path":"studies/s/cine/c/f0001.jpg","bytes":1},{"path":"studies/s/cine/c/f0002.jpg","bytes":2},{"path":"studies/s/cine/c/f0003.jpg","bytes":3}],"defaultFps":12}""");
        return new CineClipAccess(id, document.RootElement.Clone(), ["studies/s/cine/c/f0001.jpg", "studies/s/cine/c/f0002.jpg", "studies/s/cine/c/f0003.jpg"]);
    }

    private sealed class CineApplicationFactory(Guid expectedClipId, CineClipAccess? clip) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public FakeCineRepository Repository { get; } = new(expectedClipId, clip);
        public FakeSigner Signer { get; } = new();
        public CapturingAuditWriter Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>();
            services.RemoveAll<IUserProfileRoleRepository>();
            services.RemoveAll<IIdentityVerificationService>();
            services.RemoveAll<IAuditWriter>();
            services.RemoveAll<ICineRepository>();
            services.RemoveAll<ICineFrameUrlSigner>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeJwtVerifier());
            services.AddSingleton<IUserProfileRoleRepository>(new PatientRole());
            services.AddSingleton<IIdentityVerificationService>(new FakeIdentityService());
            services.AddSingleton<IAuditWriter>(Audit);
            services.AddSingleton<ICineRepository>(Repository);
            services.AddSingleton<ICineFrameUrlSigner>(Signer);
        });
    }

    private sealed class FakeCineRepository(Guid expectedClipId, CineClipAccess? clip) : ICineRepository
    {
        public List<Guid> AccountIds { get; } = [];
        public Guid? LastAccountId => AccountIds.LastOrDefault();
        public Task<CineClipAccess?> GetOwnedAsync(Guid clipId, Guid accountId, CancellationToken cancellationToken)
        {
            AccountIds.Add(accountId);
            return Task.FromResult(clipId == expectedClipId ? clip : null);
        }
    }

    private sealed class FakeSigner : ICineFrameUrlSigner
    {
        public List<string> LastPaths { get; } = [];
        public Task<IReadOnlyList<SignedFrameUrl>> MintAsync(IReadOnlyList<string> paths, int firstFrameIndex, CancellationToken cancellationToken)
        {
            LastPaths.AddRange(paths);
            return Task.FromResult<IReadOnlyList<SignedFrameUrl>>(paths.Select((path, index) => new SignedFrameUrl(firstFrameIndex + index, $"https://storage.test/{path}")).ToArray());
        }
    }

    private sealed class CapturingAuditWriter : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
    }

    private sealed class FakeJwtVerifier : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(CineApplicationFactory.UserId, true) : null); }
    private sealed class PatientRole : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient); }
    private sealed class FakeIdentityService : IIdentityVerificationService
    {
        public Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
