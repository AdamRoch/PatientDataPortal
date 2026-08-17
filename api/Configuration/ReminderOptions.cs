namespace PatientDataPortal.Api.Configuration;

public sealed class ReminderOptions
{
    public const int DefaultLeadMinutes = 24 * 60;

    public int LeadMinutes { get; set; } = DefaultLeadMinutes;

    public string PortalUrl { get; set; } = "http://localhost:3000";
}
