using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Cine;

public sealed record SignedFrameUrl(int FrameIndex, string Url);

public interface ICineFrameUrlSigner
{
    Task<IReadOnlyList<SignedFrameUrl>> MintAsync(IReadOnlyList<string> paths, int firstFrameIndex, CancellationToken cancellationToken);
}

public sealed class CineFrameUrlSigner(
    IOptions<SupabaseOptions> supabaseOptions,
    IHttpClientFactory httpClientFactory) : ICineFrameUrlSigner
{
    public const int ExpirySeconds = 300;
    private const string Bucket = "study-assets";

    public async Task<IReadOnlyList<SignedFrameUrl>> MintAsync(IReadOnlyList<string> paths, int firstFrameIndex, CancellationToken cancellationToken)
    {
        if (paths.Count == 0) return [];
        var options = supabaseOptions.Value;
        if (string.IsNullOrWhiteSpace(options.Url) || string.IsNullOrWhiteSpace(options.ServiceKey))
            throw new InvalidOperationException("SUPABASE_URL and SUPABASE_SERVICE_KEY are required to mint cine frame URLs.");

        var client = httpClientFactory.CreateClient(nameof(CineFrameUrlSigner));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Url.TrimEnd('/')}/storage/v1/object/sign/{Bucket}")
        {
            Content = JsonContent.Create(new { paths, expiresIn = ExpirySeconds }),
        };
        request.Headers.Add("apikey", options.ServiceKey);
        request.Headers.Authorization = new("Bearer", options.ServiceKey);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var signed = document.RootElement.EnumerateArray().ToArray();
        if (signed.Length != paths.Count) throw new InvalidOperationException("Storage returned an incomplete cine frame URL batch.");

        var storageBase = options.Url.TrimEnd('/') + "/storage/v1";
        return signed.Select((item, index) =>
        {
            var value = item.GetProperty("signedURL").GetString();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Storage returned a cine frame URL without signedURL.");
            var url = Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute.ToString() : storageBase + value;
            return new SignedFrameUrl(firstFrameIndex + index, url);
        }).ToArray();
    }
}
