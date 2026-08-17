namespace PatientDataPortal.Api.Profiles;

public sealed record PatientProfile(string DisplayName, string TimeZone);

public sealed record UpdatePatientProfileRequest(string? DisplayName, string? TimeZone);
