namespace PatientDataPortal.Api.Email;

public sealed record EmailSendResult(
    bool Succeeded,
    string? ProviderMessageId,
    EmailSendFailure? Failure)
{
    public static EmailSendResult Sent(string providerMessageId) => new(true, providerMessageId, null);

    public static EmailSendResult Failed(EmailSendFailure failure) => new(false, null, failure);
}

public sealed record EmailSendFailure(EmailFailureKind Kind, string Code, bool IsRetryable);

public enum EmailFailureKind
{
    Configuration,
    Rejected,
    RateLimited,
    ProviderUnavailable,
    Network,
    InvalidProviderResponse
}
