using Microsoft.AspNetCore.Authorization;

namespace PatientDataPortal.Api.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class RequireVerifiedPatientAttribute : AuthorizeAttribute
{
    public const string PolicyName = "verified-patient";
    public RequireVerifiedPatientAttribute() => Policy = PolicyName;
}
