using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using PatientDataPortal.Api.Configuration;

namespace PatientDataPortal.Api.Sharing;

public sealed record PublicShare(Guid Id, string ResourceType, string StoragePath);

public interface IPublicShareService
{
    Task<PublicShare?> FindActiveAsync(string token, CancellationToken cancellationToken);
}

public sealed class PublicShareService(IOptions<DatabaseOptions> databaseOptions, IClock clock) : IPublicShareService
{
    public async Task<PublicShare?> FindActiveAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for public shares.");

        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand("""
            SELECT share_links.id, share_links.resource_type,
                   CASE share_links.resource_type WHEN 'image' THEN images.storage_path ELSE reports.storage_path END
            FROM share_links
            LEFT JOIN images ON share_links.resource_type = 'image' AND images.id = share_links.resource_id
            LEFT JOIN studies ON images.study_id = studies.id
            LEFT JOIN reports ON share_links.resource_type = 'report' AND reports.id = share_links.resource_id
            WHERE share_links.token_hash = @token_hash
              AND share_links.revoked_at IS NULL
              AND share_links.expires_at > @now
              AND ((share_links.resource_type = 'image' AND studies.visit_status = 'completed' AND studies.performed_at IS NOT NULL AND studies.performed_at <= @now)
                   OR (share_links.resource_type = 'report' AND reports.status = 'signed' AND reports.signed_at IS NOT NULL))
            """);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("now", clock.GetCurrentInstant().ToDateTimeOffset());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PublicShare(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))
            : null;
    }
}

public interface IPublicShareStorage
{
    Task<PublicShareContent?> OpenReadAsync(PublicShare share, CancellationToken cancellationToken);
}

public sealed class PublicShareContent(Stream stream, string contentType, string fileName) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;
    public string ContentType { get; } = contentType;
    public string FileName { get; } = fileName;
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public sealed class SupabasePublicShareStorage(IHttpClientFactory httpClientFactory, IOptions<SupabaseOptions> options) : IPublicShareStorage
{
    public async Task<PublicShareContent?> OpenReadAsync(PublicShare share, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ServiceKey)) throw new InvalidOperationException("SUPABASE_SERVICE_KEY is required for public share delivery.");
        var bucket = share.ResourceType == "image" ? "study-assets" : "reports";
        var encodedPath = string.Join('/', share.StoragePath.Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"storage/v1/object/{bucket}/{encodedPath}");
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        var response = await httpClientFactory.CreateClient("supabase-storage").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return null;
        }

        var contentType = response.Content.Headers.ContentType?.ToString() ?? (share.ResourceType == "image" ? "image/png" : "application/pdf");
        var extension = share.ResourceType == "image" ? ".png" : ".pdf";
        return new PublicShareContent(new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken)), contentType, $"shared-file{extension}");
    }

    private sealed class ResponseStream(HttpResponseMessage response, Stream stream) : Stream
    {
        public override bool CanRead => stream.CanRead; public override bool CanSeek => stream.CanSeek; public override bool CanWrite => false;
        public override long Length => stream.Length; public override long Position { get => stream.Position; set => stream.Position = value; }
        public override void Flush() => stream.Flush(); public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { stream.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await stream.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this); }
    }
}

public interface IPublicShareFailureLimiter
{
    bool RecordFailure(string requestKey);
}

public sealed class PublicShareFailureLimiter(IClock clock) : IPublicShareFailureLimiter
{
    private const int MaximumFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, FailureWindow> _windows = new();

    public bool RecordFailure(string requestKey)
    {
        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        var window = _windows.AddOrUpdate(requestKey, _ => new FailureWindow(now, 1), (_, current) => current.StartedAt + Window <= now ? new FailureWindow(now, 1) : current with { Count = current.Count + 1 });
        return window.Count <= MaximumFailures;
    }

    private sealed record FailureWindow(DateTimeOffset StartedAt, int Count);
}
