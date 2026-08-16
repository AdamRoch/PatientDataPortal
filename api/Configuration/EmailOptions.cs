namespace PatientDataPortal.Api.Configuration;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Use "log" locally and in CI; use "resend" only in an environment approved to deliver email.</summary>
    public string DeliveryMode { get; set; } = "log";

    public string ApiKey { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;
}
