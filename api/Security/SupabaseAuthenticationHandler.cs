using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PatientDataPortal.Api.Security;

public sealed class SupabaseAuthenticationOptions : AuthenticationSchemeOptions;

public sealed class SupabaseAuthenticationHandler(
    IOptionsMonitor<SupabaseAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISupabaseJwtVerifier verifier,
    IAuditWriter auditWriter)
    : AuthenticationHandler<SupabaseAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "Supabase";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var userId = await verifier.VerifyAsync(header[7..].Trim(), Context.RequestAborted);
        if (userId is null) return AuthenticateResult.Fail("The bearer token is invalid or expired.");

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        identity.AddClaim(new Claim("sub", userId.Value.ToString()));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await auditWriter.WriteDeniedAsync(new AuditEvent(
            null, "anonymous", "authentication_denied", "api_route", Request.Path, "denied"), Context.RequestAborted);
        await base.HandleChallengeAsync(properties);
    }
}
