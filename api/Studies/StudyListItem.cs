namespace PatientDataPortal.Api.Studies;

public sealed record StudyListItem(Guid Id, DateTimeOffset PerformedAt, string Description, IReadOnlyList<Guid>? ImageIds = null);
