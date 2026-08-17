namespace PatientDataPortal.Api.Profiles;

public interface IPatientProfileRepository
{
    Task<PatientProfile?> GetAsync(Guid userId, CancellationToken cancellationToken);
    Task<PatientProfile?> UpdateAsync(Guid userId, string displayName, string timeZone, CancellationToken cancellationToken);
}
