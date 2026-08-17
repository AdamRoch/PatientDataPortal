using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Imaging;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class ImageEndpointTests
{
    [Fact]
    public async Task VerifiedPatientCanMintOnlyTheRequestedImageForTheirAuthenticatedAccount()
    {
        var imageId = Guid.Parse("f31380f3-d3e6-499c-aed5-c0e997bb2919");
        await using var factory = new ImageApplicationFactory(verified: true, imageId);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync($"/api/images/{imageId}");
        var access = await response.Content.ReadFromJsonAsync<ImageAccess>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(imageId, access!.Id);
        Assert.Equal(ImageApplicationFactory.UserId, factory.Images.AccountId);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task UnverifiedPatientIsDeniedBeforeAnyImageLookup()
    {
        await using var factory = new ImageApplicationFactory(verified: false, Guid.NewGuid());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync($"/api/images/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(factory.Images.AccountId);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "verified_patient_required" && audit.Result == "denied");
    }

    private sealed class ImageApplicationFactory(bool verified, Guid imageId) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public FakeImages Images { get; } = new(imageId);
        public AuditCapture Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IIdentityVerificationService>(); services.RemoveAll<IAuditWriter>(); services.RemoveAll<IImageAccessService>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeJwt()); services.AddSingleton<IUserProfileRoleRepository>(new PatientRole()); services.AddSingleton<IIdentityVerificationService>(new FakeIdentity(verified)); services.AddSingleton<IAuditWriter>(Audit); services.AddSingleton<IImageAccessService>(Images);
        });
    }

    private sealed class FakeJwt : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(ImageApplicationFactory.UserId, true) : null); }
    private sealed class PatientRole : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient); }
    private sealed class FakeIdentity(bool verified) : IIdentityVerificationService
    {
        public Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(verified);
        public Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    public sealed class FakeImages(Guid ownedImageId) : IImageAccessService
    {
        public Guid? AccountId { get; private set; }
        public Task<ImageAccess?> MintForPatientAsync(Guid imageId, Guid accountId, CancellationToken cancellationToken)
        {
            AccountId = accountId;
            return Task.FromResult<ImageAccess?>(imageId == ownedImageId ? new ImageAccess(imageId, Guid.NewGuid(), "https://storage.example.test/signed", DateTimeOffset.UtcNow.AddMinutes(5)) : null);
        }
    }
    public sealed class AuditCapture : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
    }
}
