namespace PatientDataPortal.Api.Configuration;

public sealed class OutboxOptions
{
    public string JobSecret { get; set; } = string.Empty;

    public bool BackgroundProcessingEnabled { get; set; }

    public int PollSeconds { get; set; } = 5;

    public int BatchSize { get; set; } = 100;

    public int MaximumAttempts { get; set; } = 8;

    public int LeaseMinutes { get; set; } = 5;
}
