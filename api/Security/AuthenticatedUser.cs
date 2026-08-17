namespace PatientDataPortal.Api.Security;

public sealed record AuthenticatedUser(Guid UserId, bool IsEmailVerified, string? Email = null);
