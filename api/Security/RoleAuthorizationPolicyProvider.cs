using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace PatientDataPortal.Api.Security;

public sealed class RoleAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(RequireRoleAttribute.PolicyPrefix, StringComparison.Ordinal))
            return base.GetPolicyAsync(policyName);

        var roles = policyName[RequireRoleAttribute.PolicyPrefix.Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Enum.TryParse<AppRole>(value, out var role) ? role : (AppRole?)null)
            .Where(role => role.HasValue)
            .Select(role => role!.Value)
            .ToArray();

        if (roles.Length == 0) return Task.FromResult<AuthorizationPolicy?>(null);

        return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
            .AddRequirements(new RoleRequirement(roles))
            .Build());
    }
}

public sealed record RoleRequirement(IReadOnlySet<AppRole> AllowedRoles) : IAuthorizationRequirement
{
    public RoleRequirement(IEnumerable<AppRole> allowedRoles) : this(allowedRoles.ToHashSet()) { }
}
