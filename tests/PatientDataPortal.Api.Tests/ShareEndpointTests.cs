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
using PatientDataPortal.Api.Sharing;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class ShareEndpointTests
{
    [Fact]
    public async Task VerifiedPatientCanMintAnOwnedShare()
    {
        await using var factory = new ShareApplicationFactory(verified: true, found: true);
        using var client = AuthorizedClient(factory);
        var resourceId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/api/share", new { resourceType = "image", resourceId, recipientEmail = "recipient@example.test" });
        var share = await response.Content.ReadFromJsonAsync<MintedShare>();

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(ShareApplicationFactory.UserId, factory.Shares.AccountId);
        Assert.Equal(new ShareRequest("image", resourceId, "recipient@example.test"), factory.Shares.Request);
        Assert.Equal("https://portal.example.test/share/test-token", share!.Link);
    }

    [Fact]
    public async Task UnverifiedPatientIsDeniedBeforeShareMinting()
    {
        await using var factory = new ShareApplicationFactory(verified: false, found: true);
        using var client = AuthorizedClient(factory);

        var response = await client.PostAsJsonAsync("/api/share", new { resourceType = "report", resourceId = Guid.NewGuid(), recipientEmail = "recipient@example.test" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(factory.Shares.AccountId);
    }

    [Fact]
    public async Task UnownedOrUnsignedResourceIsNotDisclosed()
    {
        await using var factory = new ShareApplicationFactory(verified: true, found: false);
        using var client = AuthorizedClient(factory);

        var response = await client.PostAsJsonAsync("/api/share", new { resourceType = "report", resourceId = Guid.NewGuid(), recipientEmail = "recipient@example.test" });

        Assert.True(response.StatusCode == HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidShareInputIsRejectedWithoutMinting()
    {
        await using var factory = new ShareApplicationFactory(verified: true, found: true);
        using var client = AuthorizedClient(factory);

        var response = await client.PostAsJsonAsync("/api/share", new { resourceType = "cine", resourceId = Guid.NewGuid(), recipientEmail = "not an email" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(factory.Shares.AccountId);
    }

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> factory) { var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", "valid"); return client; }

    private sealed class ShareApplicationFactory(bool verified, bool found) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public FakeShares Shares { get; } = new(found);
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IIdentityVerificationService>(); services.RemoveAll<IShareService>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeJwt()); services.AddSingleton<IUserProfileRoleRepository>(new PatientRole()); services.AddSingleton<IIdentityVerificationService>(new FakeIdentity(verified)); services.AddSingleton<IShareService>(Shares);
        });
    }
    private sealed class FakeJwt : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(ShareApplicationFactory.UserId, true) : null); }
    private sealed class PatientRole : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient); }
    private sealed class FakeIdentity(bool verified) : IIdentityVerificationService
    {
        public Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(verified);
        public Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class FakeShares(bool found) : IShareService
    {
        public Guid? AccountId { get; private set; }
        public ShareRequest? Request { get; private set; }
        public Task<MintedShare?> MintAsync(Guid accountId, ShareRequest request, CancellationToken cancellationToken)
        {
            AccountId = accountId; Request = request;
            return Task.FromResult<MintedShare?>(found ? new MintedShare("https://portal.example.test/share/test-token", Instant.FromUtc(2026, 8, 18, 12, 0).ToDateTimeOffset()) : null);
        }
    }
}
