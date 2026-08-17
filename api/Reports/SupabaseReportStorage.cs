using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Reports;

public sealed class SupabaseReportStorage(IHttpClientFactory httpClientFactory, IOptions<SupabaseOptions> options) : IReportStorage
{
    private const int UrlLifetimeSeconds = 60;

    public async Task<Uri> CreateSignedReadUrlAsync(string storagePath, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.ServiceKey))
            throw new InvalidOperationException("Supabase private report storage is unavailable.");

        var encodedPath = string.Join('/', storagePath.Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.Url.TrimEnd('/') + "/"), $"storage/v1/object/sign/reports/{encodedPath}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Content = JsonContent.Create(new { expiresIn = UrlLifetimeSeconds });

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Private report storage did not issue a signed URL.");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("signedURL", out var signedUrl) || string.IsNullOrWhiteSpace(signedUrl.GetString()))
            throw new InvalidOperationException("Private report storage returned an invalid signed URL.");

        var value = signedUrl.GetString()!;
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute
            : new Uri(new Uri(settings.Url.TrimEnd('/') + "/"), value.TrimStart('/'));
    }
}
