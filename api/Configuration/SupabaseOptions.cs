namespace PatientDataPortal.Api.Configuration;

public sealed class SupabaseOptions
{
    public const string SectionName = "Supabase";

    public string Url { get; set; } = string.Empty;

    public string ServiceKey { get; set; } = string.Empty;
}
