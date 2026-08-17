namespace PatientDataPortal.Api.Studies;

public interface IStudyRepository
{
    Task<IReadOnlyList<StudyListItem>> ListCompletedForPatientAsync(Guid accountId, CancellationToken cancellationToken);
}
