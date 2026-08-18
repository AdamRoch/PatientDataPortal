using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Security;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class SupabaseJwtVerifierTests
{
    [Fact]
    public async Task Supabase_user_response_maps_snake_case_identity_fields()
    {
        var userId = Guid.NewGuid();
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            Assert.Equal("https://project.supabase.co/auth/v1/user", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization.Parameter);
            Assert.Contains("anon-key", request.Headers.GetValues("apikey"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"id":"{{userId}}","email":"patient@example.com","email_confirmed_at":"2026-08-18T00:00:00Z"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        var options = Options.Create(new SupabaseOptions
        {
            Url = "https://project.supabase.co",
            AnonKey = "anon-key",
        });
        var verifier = new SupabaseJwtVerifier(client, options);

        var user = await verifier.VerifyAsync("access-token", CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal(userId, user.UserId);
        Assert.True(user.IsEmailVerified);
        Assert.Equal("patient@example.com", user.Email);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
