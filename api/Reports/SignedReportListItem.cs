namespace PatientDataPortal.Api.Reports;

// Status and storage path are deliberately omitted: this type can only represent a signed report.
public sealed record SignedReportListItem(Guid Id, DateTimeOffset SignedAt, string StudyDescription);
