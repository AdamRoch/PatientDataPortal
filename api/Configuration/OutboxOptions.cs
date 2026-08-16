namespace PatientDataPortal.Api.Configuration;

public sealed class OutboxOptions
{
    public string JobSecret { get; set; } = string.Empty;

    public int BatchSize { get; set; } = 100;

    public int MaximumAttempts { get; set; } = 8;

    public int LeaseMinutes { get; set; } = 5;
}
