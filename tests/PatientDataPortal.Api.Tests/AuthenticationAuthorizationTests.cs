using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class AuthenticationAuthorizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("expired")]
    public async Task ProtectedRoute_MissingInvalidOrExpiredToken_ReturnsUnauthorized(string? token)
    {
        await using var factory = new AuthTestApplicationFactory(AppRole.Patient);
        using var client = factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync("/WeatherForecast");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(factory.Audits.Events, audit => audit.Action == "authentication_denied" && audit.Result == "denied");
    }

    [Fact]
    public async Task ProtectedRoute_AuthenticatedWrongRole_ReturnsForbiddenAndIsAudited()
    {
        await using var factory = new AuthTestApplicationFactory(AppRole.Provider);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "valid");

        var response = await client.GetAsync("/WeatherForecast");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(factory.Audits.Events, audit => audit.Action == "authorization_denied" && audit.Result == "denied");
    }

    [Fact]
    public async Task RoleRequirement_UsesProfileRoleRatherThanAnUntrustedClaim()
    {
        var userId = Guid.NewGuid();
        var profiles = new FakeProfiles(AppRole.Provider);
        var audits = new CollectingAuditWriter();
        var contextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var handler = new RoleAuthorizationHandler(profiles, audits, contextAccessor);
        var requirement = new RoleRequirement([AppRole.Patient]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "patient")], "test"));
        var authorizationContext = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(authorizationContext);

        Assert.False(authorizationContext.HasSucceeded);
        Assert.Single(audits.Events);
    }

    private sealed class AuthTestApplicationFactory(AppRole role) : WebApplicationFactory<Program>
    {
        public CollectingAuditWriter Audits { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISupabaseJwtVerifier>();
                services.RemoveAll<IUserProfileRoleRepository>();
                services.RemoveAll<IAuditWriter>();
                services.AddSingleton<ISupabaseJwtVerifier>(new FakeVerifier());
                services.AddSingleton<IUserProfileRoleRepository>(new FakeProfiles(role));
                services.AddSingleton<IAuditWriter>(Audits);
            });
        }
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

    public sealed class CollectingAuditWriter : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = [];

        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task WriteDeniedAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
