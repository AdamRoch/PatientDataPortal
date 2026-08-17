using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Security;

public interface ISupabaseJwtVerifier
{
    Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken);
}

public sealed class SupabaseJwtVerifier(HttpClient client, IOptions<SupabaseOptions> options) : ISupabaseJwtVerifier
{
    public async Task<AuthenticatedUser?> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.Url) || string.IsNullOrWhiteSpace(configuration.AnonKey)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{configuration.Url.TrimEnd('/')}/auth/v1/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("apikey", configuration.AnonKey);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || !response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var user = await JsonSerializer.DeserializeAsync<SupabaseUser>(stream, cancellationToken: cancellationToken);
        return user is not null && Guid.TryParse(user.Id, out var userId)
            ? new AuthenticatedUser(userId, user.EmailConfirmedAt is not null, user.Email)
            : null;
    }

    private sealed record SupabaseUser(string? Id, DateTimeOffset? EmailConfirmedAt, string? Email);
}
