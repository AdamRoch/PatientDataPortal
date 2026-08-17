using Microsoft.AspNetCore.Authorization;

namespace PatientDataPortal.Api.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireRoleAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "role:";

    public RequireRoleAttribute(AppRole role) : this([role]) { }

    public RequireRoleAttribute(params AppRole[] roles) => Policy = PolicyPrefix + string.Join(',', roles);
}
