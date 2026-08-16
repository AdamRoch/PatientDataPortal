using NodaTime;
using NodaTime.Testing;
using PatientDataPortal.Api.Time;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class LockoutWindowTests
{
    [Fact]
    public void LockoutExpiresWhenTheInjectedClockAdvancesPastFifteenMinutes()
    {
        var clock = new FakeClock(Instant.FromUtc(2026, 8, 16, 12, 0));
        var lockout = new LockoutWindow(clock);
        var lockedUntil = clock.GetCurrentInstant() + Duration.FromMinutes(15);

        Assert.True(lockout.IsActive(lockedUntil));

        clock.Advance(Duration.FromMinutes(15));

        Assert.False(lockout.IsActive(lockedUntil));
    }
}
