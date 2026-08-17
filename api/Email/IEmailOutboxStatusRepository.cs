namespace PatientDataPortal.Api.Email;

public sealed record EmailOutboxStatusItem(
    string Kind,
    string Status,
    int Attempts,
    DateTimeOffset DueAt,
    DateTimeOffset? SentAt,
    string? ProviderMessageId);

public interface IEmailOutboxStatusRepository
{
    Task<IReadOnlyList<EmailOutboxStatusItem>> ListAsync(CancellationToken cancellationToken);
}
