namespace PatientDataPortal.Api.Configuration;

public sealed class IdentityVerificationOptions
{
    public string HmacKey { get; set; } = string.Empty;
}
