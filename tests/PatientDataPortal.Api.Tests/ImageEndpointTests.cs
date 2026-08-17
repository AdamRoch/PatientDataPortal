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
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(factory.Images.AccountId);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "verified_patient_required" && audit.Result == "denied");
    }

    [Fact]
    public async Task ForeignJwtCannotMintAnotherPatientsImageBytesAndIsAudited()
    {
        var imageId = Guid.NewGuid();
        await using var factory = new ImageApplicationFactory(verified: true, imageId);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "foreign");

        var response = await client.GetAsync($"/api/images/{imageId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(ImageApplicationFactory.OtherUserId, factory.Images.AccountId);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "content_access_denied" && audit.TargetType == "image" && audit.TargetReference == imageId.ToString() && audit.Result == "denied");
    }

    private sealed class ImageApplicationFactory(bool verified, Guid imageId) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("7494cb41-69d6-4a86-8cec-a8d82da7b957");
        public static readonly Guid OtherUserId = Guid.Parse("052e7848-3763-40f7-b45a-7c8c38320788");
        public FakeImages Images { get; } = new(imageId);
        public AuditCapture Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IIdentityVerificationService>(); services.RemoveAll<IAuditWriter>(); services.RemoveAll<IImageAccessService>();
            Images.Audit = Audit;
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeJwt()); services.AddSingleton<IUserProfileRoleRepository>(new PatientRole()); services.AddSingleton<IIdentityVerificationService>(new FakeIdentity(verified)); services.AddSingleton<IAuditWriter>(Audit); services.AddSingleton<IImageAccessService>(Images);
        });
    }

    private sealed class FakeJwt : ISupabaseJwtVerifier
    {
        public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token switch
        {
            "valid" => new AuthenticatedUser(ImageApplicationFactory.UserId, true),
            "foreign" => new AuthenticatedUser(ImageApplicationFactory.OtherUserId, true),
            _ => null,
        });
    }
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
        public AuditCapture? Audit { get; set; }
        public async Task<ImageAccess?> MintForPatientAsync(Guid imageId, Guid accountId, CancellationToken cancellationToken)
        {
            AccountId = accountId;
            if (accountId == ImageApplicationFactory.UserId && imageId == ownedImageId)
                return new ImageAccess(imageId, Guid.NewGuid(), "https://storage.example.test/signed", DateTimeOffset.UtcNow.AddMinutes(5));

            await Audit!.WriteDeniedAsync(new AuditEvent(accountId.ToString(), "patient", "content_access_denied", "image", imageId.ToString(), "denied"), cancellationToken);
            return null;
        }
    }
    public sealed class AuditCapture : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
    }
}
