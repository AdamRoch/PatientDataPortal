namespace PatientDataPortal.Api.Reports;

public interface IReportStorage
{
    Task<Uri> CreateSignedReadUrlAsync(string storagePath, CancellationToken cancellationToken);
}
