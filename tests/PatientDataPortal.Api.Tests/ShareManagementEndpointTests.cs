using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Identity;
using PatientDataPortal.Api.Security;
using PatientDataPortal.Api.Sharing;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class ShareManagementEndpointTests
{
    [Fact]
    public async Task PatientCanListAndRevokeOnlyTheirOwnShare()
    {
        await using var factory = new ManagementApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");

        var shares = await client.GetFromJsonAsync<List<ManagedShare>>("/api/shares");
        var revoke = await client.DeleteAsync($"/api/shares/{factory.Shares.OwnedShareId}");
        var foreign = await client.DeleteAsync($"/api/shares/{Guid.NewGuid()}");

        Assert.Single(shares!);
        Assert.Equal(ManagementApplicationFactory.UserId, factory.Shares.ListedFor);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(ManagementApplicationFactory.UserId, factory.Shares.RevokedFor);
        Assert.Contains(factory.Audit.Events, audit => audit.Action == "share_list_viewed" && audit.TargetType == "share_link" && audit.Result == "allowed");
    }

    private sealed class ManagementApplicationFactory : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("d9af9bf7-c76d-4cc8-a3f4-66e89224e66a");
        public FakeManagementShares Shares { get; } = new();
        public CapturingAudit Audit { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IIdentityVerificationService>(); services.RemoveAll<IShareManagementService>(); services.RemoveAll<IAuditWriter>();
            services.AddSingleton<ISupabaseJwtVerifier>(new FakeJwt()); services.AddSingleton<IUserProfileRoleRepository>(new PatientRole()); services.AddSingleton<IIdentityVerificationService>(new VerifiedIdentity()); services.AddSingleton<IShareManagementService>(Shares); services.AddSingleton<IAuditWriter>(Audit);
        });
    }
    private sealed class FakeJwt : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(ManagementApplicationFactory.UserId, true) : null); }
    private sealed class PatientRole : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(AppRole.Patient); }
    private sealed class VerifiedIdentity : IIdentityVerificationService { public Task<IdentityVerificationResult> VerifyAsync(Guid accountId, bool emailVerified, IdentityVerificationRequest request, string networkIdentity, CancellationToken cancellationToken) => throw new NotSupportedException(); public Task<bool> IsVerifiedPatientAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(true); public Task RecoverClaimAsync(Guid patientRecordId, Guid adminId, string? reasonCode, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class FakeManagementShares : IShareManagementService
    {
        public Guid OwnedShareId { get; } = Guid.NewGuid(); public Guid? ListedFor { get; private set; } public Guid? RevokedFor { get; private set; }
        public Task<IReadOnlyList<ManagedShare>> ListAsync(Guid accountId, CancellationToken cancellationToken) { ListedFor = accountId; return Task.FromResult<IReadOnlyList<ManagedShare>>([new(OwnedShareId, "report", Guid.NewGuid(), "recipient@example.test", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow, null, "active")]); }
        public Task<bool> RevokeAsync(Guid accountId, Guid shareId, CancellationToken cancellationToken) { RevokedFor = accountId; return Task.FromResult(shareId == OwnedShareId); }
    }
    private sealed class CapturingAudit : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken) { Events.Add(auditEvent); return Task.CompletedTask; }
    }
}
