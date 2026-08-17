namespace PatientDataPortal.Api.Reports;

public interface IReportRepository
{
    Task<IReadOnlyList<SignedReportListItem>> ListSignedForPatientAsync(Guid accountId, CancellationToken cancellationToken);
    Task<SignedReportStorageItem?> FindSignedForPatientAsync(Guid reportId, Guid accountId, CancellationToken cancellationToken);
}
