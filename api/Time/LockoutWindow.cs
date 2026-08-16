using NodaTime;

namespace PatientDataPortal.Api.Time;

/// <summary>Small shared seam for policies that must compare a durable expiry to the current instant.</summary>
public sealed class LockoutWindow(IClock clock)
{
    public bool IsActive(Instant lockedUntil) => clock.GetCurrentInstant() < lockedUntil;
}
