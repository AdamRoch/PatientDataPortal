using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using PatientDataPortal.Api.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace PatientDataPortal.Api.Seeding;

/// <summary>Creates only synthetic, deterministic imaging and report fixtures for local and demo environments.</summary>
public sealed class ImagingSeedGenerator
{
    public const int PatientCount = 50;
    public const long StorageBudgetBytes = 1_000_000_000;
    private const string StudyAssetsBucket = "study-assets";
    private const string ReportsBucket = "reports";
    private const int FrameBytes = 40 * 1024;
    private const int ThumbnailBytes = 8 * 1024;

    public static ImagingSeedSummary DescribePlan() => ImagingSeedPlan.Create().ToSummary();

    public async Task<ImagingSeedSummary> SeedAsync(CancellationToken cancellationToken = default)
    {
        var databaseUrl = RequiredEnvironment("DATABASE_URL");
        var supabaseUrl = RequiredEnvironment("SUPABASE_URL").TrimEnd('/');
        var serviceKey = RequiredEnvironment("SUPABASE_SERVICE_KEY");
        var plan = ImagingSeedPlan.Create();
        if (plan.TotalBytes >= StorageBudgetBytes)
            throw new InvalidOperationException($"Seed plan is {plan.TotalBytes} bytes, above the {StorageBudgetBytes} byte budget.");

        await using var dataSource = NpgsqlDataSource.Create(DatabaseConnectionString.Normalize(databaseUrl));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var patient in plan.Patients)
            await UpsertPatientAsync(connection, transaction, patient, cancellationToken);
        foreach (var study in plan.Studies)
            await UpsertStudyAsync(connection, transaction, study, cancellationToken);
        foreach (var image in plan.Images)
            await UpsertImageAsync(connection, transaction, image, cancellationToken);
        foreach (var clip in plan.Clips)
            await UpsertClipAsync(connection, transaction, clip, cancellationToken);
        foreach (var report in plan.Reports)
            await UpsertReportAsync(connection, transaction, report, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri(supabaseUrl + "/") };
        http.DefaultRequestHeaders.Add("apikey", serviceKey);
        http.DefaultRequestHeaders.Authorization = new("Bearer", serviceKey);
        await EnsurePrivateBucketAsync(http, StudyAssetsBucket, cancellationToken);
        foreach (var asset in plan.ImagingAssets)
            await UploadAsync(http, StudyAssetsBucket, asset, cancellationToken);
        await EnsurePrivateBucketAsync(http, ReportsBucket, cancellationToken);
        foreach (var asset in plan.ReportAssets)
            await UploadAsync(http, ReportsBucket, asset, cancellationToken);

        var verifiedPatients = await CountAsync(connection, "patient_records", plan.Patients.Select(patient => patient.Id), cancellationToken);
        var verifiedStudies = await CountAsync(connection, "studies", plan.Studies.Select(study => study.Id), cancellationToken);
        var verifiedImages = await CountAsync(connection, "images", plan.Images.Select(image => image.Id), cancellationToken);
        var verifiedClips = await CountAsync(connection, "cine_clips", plan.Clips.Select(clip => clip.Id), cancellationToken);
        var verifiedReports = await CountAsync(connection, "reports", plan.Reports.Select(report => report.Id), cancellationToken);
        var summary = plan.ToSummary() with { VerifiedPatients = verifiedPatients, VerifiedStudies = verifiedStudies, VerifiedImages = verifiedImages, VerifiedCineClips = verifiedClips, VerifiedReports = verifiedReports };
        if (verifiedPatients != summary.Patients || verifiedStudies != summary.TotalStudies || verifiedImages != summary.Images || verifiedClips != summary.CineClips || verifiedReports != summary.TotalReports)
            throw new InvalidOperationException("Imaging seed verification failed.");
        return summary;
    }

    private static async Task UpsertPatientAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SeedPatient patient, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO patient_records (id, patient_ref, dob, full_name)
            VALUES (@id, @reference, @dob, @name)
            ON CONFLICT (id) DO UPDATE SET patient_ref = EXCLUDED.patient_ref, dob = EXCLUDED.dob, full_name = EXCLUDED.full_name
            """;
        await ExecuteAsync(connection, transaction, sql, cancellationToken, ("id", patient.Id), ("reference", patient.Reference), ("dob", patient.DateOfBirth), ("name", patient.Name));
    }

    private static async Task UpsertStudyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SeedStudy study, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO studies (id, patient_record_id, performed_at, visit_status, description)
            VALUES (@id, @patientId, @performedAt, @status, @description)
            ON CONFLICT (id) DO UPDATE SET patient_record_id = EXCLUDED.patient_record_id, performed_at = EXCLUDED.performed_at, visit_status = EXCLUDED.visit_status, description = EXCLUDED.description
            """;
        await ExecuteAsync(connection, transaction, sql, cancellationToken, ("id", study.Id), ("patientId", study.PatientId), ("performedAt", study.PerformedAt), ("status", study.Status), ("description", study.Description));
    }

    private static Task UpsertImageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SeedImage image, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO images (id, study_id, storage_path, thumbnail_path) VALUES (@id, @studyId, @path, @thumbnail) ON CONFLICT (id) DO UPDATE SET study_id = EXCLUDED.study_id, storage_path = EXCLUDED.storage_path, thumbnail_path = EXCLUDED.thumbnail_path", cancellationToken, ("id", image.Id), ("studyId", image.StudyId), ("path", image.Path), ("thumbnail", image.ThumbnailPath));

    private static Task UpsertClipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SeedClip clip, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO cine_clips (id, study_id, storage_path, frame_count, manifest) VALUES (@id, @studyId, @path, @frameCount, CAST(@manifest AS jsonb)) ON CONFLICT (id) DO UPDATE SET study_id = EXCLUDED.study_id, storage_path = EXCLUDED.storage_path, frame_count = EXCLUDED.frame_count, manifest = EXCLUDED.manifest", cancellationToken, ("id", clip.Id), ("studyId", clip.StudyId), ("path", clip.ManifestPath), ("frameCount", clip.FrameCount), ("manifest", clip.Manifest));

    private static Task UpsertReportAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SeedReport report, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "INSERT INTO reports (id, patient_record_id, study_id, status, signed_at, signed_by, storage_path) VALUES (@id, @patientId, @studyId, @status, @signedAt, @signedBy, @path) ON CONFLICT (id) DO UPDATE SET patient_record_id = EXCLUDED.patient_record_id, study_id = EXCLUDED.study_id, status = EXCLUDED.status, signed_at = EXCLUDED.signed_at, signed_by = EXCLUDED.signed_by, storage_path = EXCLUDED.storage_path", cancellationToken, ("id", report.Id), ("patientId", report.PatientId), ("studyId", report.StudyId), ("status", report.Status), ("signedAt", report.SignedAt), ("signedBy", report.SignedBy), ("path", report.Path));

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsurePrivateBucketAsync(HttpClient http, string bucket, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "storage/v1/bucket") { Content = JsonContent.Create(new { id = bucket, name = bucket, @public = false }) };
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)
            throw new HttpRequestException($"Could not create private {bucket} bucket: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");

        using var bucketResponse = await http.GetAsync($"storage/v1/bucket/{bucket}", cancellationToken);
        bucketResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await bucketResponse.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("public", out var isPublic) || isPublic.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException($"Storage bucket {bucket} must be private before seeding fixtures.");
    }

    private static async Task UploadAsync(HttpClient http, string bucket, SeedAsset asset, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"storage/v1/object/{bucket}/{asset.Path}")
        {
            Content = new ByteArrayContent(asset.Bytes),
        };
        request.Content.Headers.ContentType = new(asset.ContentType);
        request.Headers.Add("x-upsert", "true");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<int> CountAsync(NpgsqlConnection connection, string table, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"{name} must be set before running --seed-imaging.");

    private sealed record SeedPatient(Guid Id, string Reference, DateOnly DateOfBirth, string Name);
    private sealed record SeedStudy(Guid Id, Guid PatientId, DateTimeOffset? PerformedAt, string Status, string Description);
    private sealed record SeedImage(Guid Id, Guid StudyId, string Path, string ThumbnailPath);
    private sealed record SeedClip(Guid Id, Guid StudyId, string ManifestPath, int FrameCount, string Manifest);
    private sealed record SeedReport(Guid Id, Guid PatientId, Guid StudyId, string Status, DateTimeOffset? SignedAt, Guid? SignedBy, string Path);

    private sealed class ImagingSeedPlan
    {
        public List<SeedPatient> Patients { get; } = [];
        public List<SeedStudy> Studies { get; } = [];
        public List<SeedImage> Images { get; } = [];
        public List<SeedClip> Clips { get; } = [];
        public List<SeedReport> Reports { get; } = [];
        public List<SeedAsset> ImagingAssets { get; } = [];
        public List<SeedAsset> ReportAssets { get; } = [];
        public long TotalBytes => ImagingAssets.Concat(ReportAssets).Sum(asset => (long)asset.Bytes.Length);

        public static ImagingSeedPlan Create()
        {
            var plan = new ImagingSeedPlan();
            var hundredFrameClips = 0;
            for (var patientNumber = 1; patientNumber <= PatientCount; patientNumber++)
            {
                var patient = new SeedPatient(IdFor($"patient:{patientNumber}"), $"SYN-{patientNumber:0000}", new DateOnly(1960 + patientNumber % 45, patientNumber % 12 + 1, patientNumber % 27 + 1), $"Synthetic Patient {patientNumber:000}");
                plan.Patients.Add(patient);
                var completedStudies = 1 + StableNumber($"completed:{patientNumber}", 5);
                for (var visit = 1; visit <= completedStudies; visit++)
                    AddStudy(plan, patient, visit, "completed", new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(-(patientNumber * 7 + visit)), ref hundredFrameClips);
                if (patientNumber <= 12)
                    AddReports(plan, patient);
                if (patientNumber % 10 == 0) AddStudy(plan, patient, 90, "scheduled", null, ref hundredFrameClips);
                if (patientNumber % 12 == 0) AddStudy(plan, patient, 91, "cancelled", null, ref hundredFrameClips);
            }
            return plan;
        }

        private static void AddStudy(ImagingSeedPlan plan, SeedPatient patient, int visit, string status, DateTimeOffset? performedAt, ref int hundredFrameClips)
        {
            var study = new SeedStudy(IdFor($"study:{patient.Id}:{visit}"), patient.Id, performedAt, status, status == "completed" ? "Synthetic ultrasound follow-up" : $"Synthetic {status} ultrasound study");
            plan.Studies.Add(study);
            var imageCount = status == "completed" ? 1 + StableNumber($"images:{study.Id}", 10) : 2;
            for (var imageNumber = 1; imageNumber <= imageCount; imageNumber++)
            {
                var image = new SeedImage(IdFor($"image:{study.Id}:{imageNumber}"), study.Id, $"studies/{study.Id}/images/{IdFor($"image:{study.Id}:{imageNumber}")}.jpg", $"studies/{study.Id}/thumbnails/{IdFor($"image:{study.Id}:{imageNumber}")}.jpg");
                plan.Images.Add(image);
                plan.ImagingAssets.Add(new SeedAsset(image.Path, "image/jpeg", SyntheticJpeg($"image:{image.Id}", FrameBytes, 256)));
                plan.ImagingAssets.Add(new SeedAsset(image.ThumbnailPath, "image/jpeg", SyntheticJpeg($"thumbnail:{image.Id}", ThumbnailBytes, 96)));
            }
            if (status != "completed") return;
            var clipCount = StableNumber($"clips:{study.Id}", 3);
            for (var clipNumber = 1; clipNumber <= clipCount; clipNumber++)
            {
                var clipId = IdFor($"clip:{study.Id}:{clipNumber}");
                var frameCount = hundredFrameClips < 2 ? 100 : 20 + StableNumber($"frames:{clipId}", 41);
                if (frameCount == 100) hundredFrameClips++;
                var manifestPath = $"studies/{study.Id}/cine/{clipId}/manifest.json";
                var frames = new List<object>(frameCount);
                var isDeliberatelyPartialClip = plan.Clips.Count == 0;
                for (var frame = 1; frame <= frameCount; frame++)
                {
                    var path = $"studies/{study.Id}/cine/{clipId}/f{frame:0000}.jpg";
                    var bytes = SyntheticJpeg($"cine:{clipId}:{frame}", FrameBytes, 256);
                    if (!isDeliberatelyPartialClip || frame != 2)
                        plan.ImagingAssets.Add(new SeedAsset(path, "image/jpeg", bytes));
                    frames.Add(new { path, bytes = bytes.Length });
                }
                var manifest = JsonSerializer.Serialize(new { frames, defaultFps = 12 });
                plan.Clips.Add(new SeedClip(clipId, study.Id, manifestPath, frameCount, manifest));
                plan.ImagingAssets.Add(new SeedAsset(manifestPath, "application/json", Encoding.UTF8.GetBytes(manifest)));
            }
        }

        private static void AddReports(ImagingSeedPlan plan, SeedPatient patient)
        {
            var study = plan.Studies.First(candidate => candidate.PatientId == patient.Id && candidate.Status == "completed");
            var signer = IdFor("synthetic-report-signer");
            foreach (var status in new[] { "signed", "preliminary" })
            {
                var reportId = IdFor($"report:{patient.Id}:{status}");
                var signed = status == "signed";
                var report = new SeedReport(reportId, patient.Id, study.Id, status, signed ? study.PerformedAt!.Value.AddHours(2) : null, signed ? signer : null, $"reports/{reportId}.pdf");
                plan.Reports.Add(report);
                plan.ReportAssets.Add(new SeedAsset(report.Path, "application/pdf", SyntheticPdf(report.Id, report.Status)));
            }
        }

        public ImagingSeedSummary ToSummary() => new(Patients.Count, Studies.Count(study => study.Status == "completed"), Studies.Count(study => study.Status == "scheduled"), Studies.Count(study => study.Status == "cancelled"), Images.Count, Clips.Count, Clips.Count(clip => clip.FrameCount == 100), Reports.Count(report => report.Status == "signed"), Reports.Count(report => report.Status == "preliminary"), 1, ImagingAssets.Count + ReportAssets.Count, TotalBytes, StorageBudgetBytes);
    }

    private static Guid IdFor(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes("PTDP-17:" + value)).AsSpan(0, 16));
    private static int StableNumber(string value, int exclusiveMaximum) => SHA256.HashData(Encoding.UTF8.GetBytes("PTDP-17:" + value))[0] % exclusiveMaximum;

    private static byte[] SyntheticJpeg(string key, int targetBytes, int size)
    {
        var random = SHA256.HashData(Encoding.UTF8.GetBytes("PTDP-17:" + key));
        using var image = new Image<L8>(size, size);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < size; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < size; x++)
                {
                    var dx = (x - size / 2.0) / (size / 2.0);
                    var dy = (y - size / 2.0) / (size / 2.0);
                    var radius = Math.Sqrt(dx * dx + dy * dy);
                    var noise = random[(x * 17 + y * 31) % random.Length] % 36;
                    var brightness = radius > 0.92 ? 4 : Math.Clamp((int)(142 - radius * 72 + noise + 30 * Math.Sin((x + y + random[0]) / 18.0)), 0, 255);
                    row[x] = new L8((byte)brightness);
                }
            }
        });
        using var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder { Quality = 72 });
        var bytes = stream.ToArray();
        if (bytes.Length > targetBytes) throw new InvalidOperationException($"Synthetic frame unexpectedly exceeded {targetBytes} bytes.");
        Array.Resize(ref bytes, targetBytes);
        return bytes;
    }

    private static byte[] SyntheticPdf(Guid reportId, string status)
    {
        var text = $"SYNTHETIC DEMO ONLY - NOT A CLINICAL REPORT - {status.ToUpperInvariant()} - fixture {reportId}";
        var stream = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
        };
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, Encoding.ASCII, 1024, leaveOpen: true);
        writer.Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            writer.Flush();
            offsets.Add(output.Position);
            writer.Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        writer.Flush();
        var xref = output.Position;
        writer.Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) writer.Write($"{offset:0000000000} 00000 n \n");
        writer.Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();
        return output.ToArray();
    }
}

public sealed record SeedAsset(string Path, string ContentType, byte[] Bytes);

public sealed record ImagingSeedSummary(int Patients, int CompletedStudies, int ScheduledStudies, int CancelledStudies, int Images, int CineClips, int HundredFrameClips, int SignedReports, int PreliminaryReports, int CineClipsWithMissingFrames, int StorageObjects, long StorageBytes, long StorageBudgetBytes, int VerifiedPatients = 0, int VerifiedStudies = 0, int VerifiedImages = 0, int VerifiedCineClips = 0, int VerifiedReports = 0)
{
    public int TotalStudies => CompletedStudies + ScheduledStudies + CancelledStudies;
    public int TotalReports => SignedReports + PreliminaryReports;
    public string ToLogLine() => string.Create(CultureInfo.InvariantCulture, $"imaging-seed patients={Patients} completed_studies={CompletedStudies} scheduled_studies={ScheduledStudies} cancelled_studies={CancelledStudies} images={Images} cine_clips={CineClips} hundred_frame_clips={HundredFrameClips} signed_reports={SignedReports} preliminary_reports={PreliminaryReports} cine_clips_with_missing_frames={CineClipsWithMissingFrames} storage_objects={StorageObjects} storage_bytes={StorageBytes} storage_budget_bytes={StorageBudgetBytes} verified_patients={VerifiedPatients} verified_studies={VerifiedStudies} verified_images={VerifiedImages} verified_cine_clips={VerifiedCineClips} verified_reports={VerifiedReports}");
}
