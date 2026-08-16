namespace PatientDataPortal.Api.Email;

public sealed record EmailMessage(
    string To,
    string Subject,
    string Html,
    string IdempotencyKey,
    string? Text = null);
