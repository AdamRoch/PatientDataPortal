using System.Text.Json;

namespace PatientDataPortal.Api.Cine;

public sealed record CineClipAccess(Guid Id, JsonElement Manifest, IReadOnlyList<string> FramePaths);

public interface ICineRepository
{
    Task<CineClipAccess?> GetOwnedAsync(Guid clipId, Guid accountId, CancellationToken cancellationToken);
}
