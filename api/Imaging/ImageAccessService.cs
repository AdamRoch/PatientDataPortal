using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using PatientDataPortal.Api.Security;

namespace PatientDataPortal.Api.Imaging;

public sealed record ImageAccess(Guid Id, Guid StudyId, string SignedUrl, DateTimeOffset ExpiresAt);

public interface IImageAccessService
{
    Task<ImageAccess?> MintForPatientAsync(Guid imageId, Guid accountId, CancellationToken cancellationToken);
}

public sealed class ImageAccessService(
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<SupabaseOptions> supabaseOptions,
    IHttpClientFactory httpClientFactory,
    IAuditWriter auditWriter) : IImageAccessService
{
    private const int SignedUrlTtlSeconds = 300;
    private const string Bucket = "study-assets";

    public async Task<ImageAccess?> MintForPatientAsync(Guid imageId, Guid accountId, CancellationToken cancellationToken)
    {
        var storagePath = await FindOwnedCompletedImageAsync(imageId, accountId, cancellationToken);
        if (storagePath is null)
        {
            await auditWriter.WriteDeniedAsync(new AuditEvent(accountId.ToString(), "patient", "content_access_denied", "image", imageId.ToString(), "denied"), cancellationToken);
            return null;
        }

        var options = supabaseOptions.Value;
        if (string.IsNullOrWhiteSpace(options.Url) || string.IsNullOrWhiteSpace(options.ServiceKey))
            throw new InvalidOperationException("SUPABASE_URL and SUPABASE_SERVICE_KEY are required for image delivery.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"storage/v1/object/sign/{Bucket}/{storagePath}")
        {
            Content = JsonContent.Create(new { expiresIn = SignedUrlTtlSeconds }),
        };
        request.Headers.Add("apikey", options.ServiceKey);
        request.Headers.Authorization = new("Bearer", options.ServiceKey);
        using var response = await httpClientFactory.CreateClient("supabase-storage").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var signed = await response.Content.ReadFromJsonAsync<SignedObject>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Storage did not return a signed image URL.");
        if (string.IsNullOrWhiteSpace(signed.SignedUrl)) throw new InvalidOperationException("Storage returned an empty signed image URL.");

        await auditWriter.WriteAsync(new AuditEvent(accountId.ToString(), "patient", "content_access_granted", "image", imageId.ToString(), "allowed"), cancellationToken);
        return new ImageAccess(imageId, storagePath.StudyId, new Uri(new Uri(options.Url.TrimEnd('/') + "/"), "storage/v1" + signed.SignedUrl).ToString(), DateTimeOffset.UtcNow.AddSeconds(SignedUrlTtlSeconds));
    }

    private async Task<OwnedImage?> FindOwnedCompletedImageAsync(Guid imageId, Guid accountId, CancellationToken cancellationToken)
    {
        var connectionString = databaseOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("DATABASE_URL is required for images.");

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(connectionString));
        await using var command = dataSource.CreateCommand("""
            SELECT images.storage_path, studies.id
            FROM images
            INNER JOIN studies ON studies.id = images.study_id
            INNER JOIN patient_records ON patient_records.id = studies.patient_record_id
            WHERE images.id = @image_id
              AND patient_records.claimed_by = @account_id
              AND studies.visit_status = 'completed'
              AND studies.performed_at IS NOT NULL
              AND studies.performed_at <= CURRENT_TIMESTAMP
            """);
        command.Parameters.AddWithValue("image_id", imageId);
        command.Parameters.AddWithValue("account_id", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new OwnedImage(reader.GetString(0), reader.GetGuid(1)) : null;
    }

    private sealed record OwnedImage(string StoragePath, Guid StudyId);
    private sealed record SignedObject(string SignedUrl);
}
