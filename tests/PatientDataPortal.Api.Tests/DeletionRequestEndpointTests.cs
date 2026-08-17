using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Deletion;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class DeletionRequestEndpointTests
{
    [Fact]
    public async Task PatientCanSubmitOwnRequestAndAdminCanListPendingRequests()
    {
        await using var patientFactory = new Factory(AppRole.Patient);
        using var patient = patientFactory.CreateClient(); patient.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        var submitted = await patient.PostAsync("/api/deletion-requests", null);
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        Assert.Equal(Factory.UserId, patientFactory.Requests.RequestedBy);
        Assert.Contains(patientFactory.Audit.Events, item => item.Action == "deletion_requested" && item.ActorReference is null && item.TargetReference != "2b01d3c9-3483-4841-9378-eab090649bd3");

        await using var adminFactory = new Factory(AppRole.Admin);
        using var admin = adminFactory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        var rows = await admin.GetFromJsonAsync<List<AdminDeletionRequest>>("/api/admin/deletion-requests");
        Assert.Single(rows!);
        Assert.Contains(adminFactory.Audit.Events, item => item.Action == "deletion_requests_view");
    }

    [Fact]
    public async Task NonPatientCannotSubmitAndNonAdminCannotList()
    {
        await using var providerFactory = new Factory(AppRole.Provider);
        using var provider = providerFactory.CreateClient(); provider.DefaultRequestHeaders.Authorization = new("Bearer", "valid");
        Assert.Equal(HttpStatusCode.Forbidden, (await provider.PostAsync("/api/deletion-requests", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await provider.GetAsync("/api/admin/deletion-requests")).StatusCode);
        Assert.Null(providerFactory.Requests.RequestedBy);
    }

    private sealed class Factory(AppRole role) : WebApplicationFactory<Program>
    {
        public static readonly Guid UserId = Guid.Parse("d9af9bf7-c76d-4cc8-a3f4-66e89224e66a");
        public FakeRequests Requests { get; } = new(); public FakeAudit Audit { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISupabaseJwtVerifier>(); services.RemoveAll<IUserProfileRoleRepository>(); services.RemoveAll<IDeletionRequestService>(); services.RemoveAll<IAuditWriter>();
            services.AddSingleton<ISupabaseJwtVerifier>(new Jwt()); services.AddSingleton<IUserProfileRoleRepository>(new Roles(role)); services.AddSingleton<IDeletionRequestService>(Requests); services.AddSingleton<IAuditWriter>(Audit);
        });
    }
    private sealed class Jwt : ISupabaseJwtVerifier { public Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken) => Task.FromResult(token == "valid" ? new AuthenticatedUser(Factory.UserId, true) : null); }
    private sealed class Roles(AppRole role) : IUserProfileRoleRepository { public Task<AppRole?> GetRoleAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<AppRole?>(role); }
    private sealed class FakeRequests : IDeletionRequestService
    {
        public Guid? RequestedBy { get; private set; }
        public Task<DeletionRequest?> RequestAsync(Guid accountId, CancellationToken cancellationToken) { RequestedBy = accountId; return Task.FromResult<DeletionRequest?>(new(Guid.Parse("2b01d3c9-3483-4841-9378-eab090649bd3"), "pending", DateTimeOffset.Parse("2026-08-16T12:00:00Z"), Guid.Parse("1174127e-c28b-43da-a807-6ff2d99ba1b3"))); }
        public Task<IReadOnlyList<AdminDeletionRequest>> ListPendingAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AdminDeletionRequest>>([new(Guid.Parse("2b01d3c9-3483-4841-9378-eab090649bd3"), "pending", DateTimeOffset.Parse("2026-08-16T12:00:00Z"), "SYN-0001")]);
    }
    private sealed class FakeAudit : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent item, CancellationToken cancellationToken) { Events.Add(item); return Task.CompletedTask; }
        public Task WriteDeniedAsync(AuditEvent item, CancellationToken cancellationToken) { Events.Add(item); return Task.CompletedTask; }
        public Task WriteAllowedAsync(AuditEvent item, CancellationToken cancellationToken) { Events.Add(item); return Task.CompletedTask; }
    }
}
