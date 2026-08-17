using NodaTime;
using PatientDataPortal.Api.Scheduling;
using Xunit;

namespace PatientDataPortal.Api.Tests;

public sealed class SlotGeneratorTests
{
    [Fact]
    public void NewYorkOvernightHoursUseCorrectInstantsAcrossBothDstTransitions()
    {
        var rules = new[]
        {
            Rule(new DateOnly(2030, 3, 10), 0, "00:00", "04:00"),
            Rule(new DateOnly(2030, 11, 3), 0, "00:00", "04:00")
        };

        var spring = SlotGenerator.Generate(Instant.FromUtc(2030, 3, 9, 0, 0), "America/New_York", 60, rules, [])
            .Where(slot => slot.StartsAt.InUtc().Date == new LocalDate(2030, 3, 10)).ToArray();
        var fall = SlotGenerator.Generate(Instant.FromUtc(2030, 11, 2, 0, 0), "America/New_York", 60, rules, [])
            .Where(slot => slot.StartsAt.InUtc().Date == new LocalDate(2030, 11, 3)).ToArray();

        Assert.Equal(["2030-03-10T05:00:00Z", "2030-03-10T06:00:00Z", "2030-03-10T07:00:00Z"], UtcStarts(spring));
        Assert.Equal(["2030-11-03T04:00:00Z", "2030-11-03T05:00:00Z", "2030-11-03T07:00:00Z", "2030-11-03T08:00:00Z"], UtcStarts(fall));
        Assert.Equal(spring.Length, spring.Select(slot => slot.StartsAt).Distinct().Count());
        Assert.Equal(fall.Length, fall.Select(slot => slot.StartsAt).Distinct().Count());
    }

    [Fact]
    public void AppliesRulesSlotLengthAndBlockedRangesWithoutCrossingProviderData()
    {
        var date = new DateOnly(2030, 1, 7);
        var rules = new[] { Rule(date, 1, "09:00", "11:00") };
        var blocked = new[] { new BlockedTime(Guid.NewGuid(), Instant.FromUtc(2030, 1, 7, 15, 30), Instant.FromUtc(2030, 1, 7, 16, 0)) };

        var slots = SlotGenerator.Generate(Instant.FromUtc(2030, 1, 6, 0, 0), "America/New_York", 30, rules, blocked);

        Assert.Equal(["2030-01-07T14:00:00Z", "2030-01-07T14:30:00Z", "2030-01-07T15:00:00Z"], UtcStarts(slots));
        Assert.All(slots, slot => Assert.Equal(Duration.FromMinutes(30), slot.EndsAt - slot.StartsAt));
    }

    [Fact]
    public void RegenerationDeletesOnlyThisProvidersOpenSlotsAndPreservesBookedSlots()
    {
        var provider = Guid.NewGuid();
        var otherProvider = Guid.NewGuid();
        var now = Instant.FromUtc(2030, 1, 1, 0, 0);
        var bookedStart = Instant.FromUtc(2030, 1, 2, 14, 0);
        var freshStart = Instant.FromUtc(2030, 1, 2, 15, 0);
        var otherOpenId = Guid.NewGuid();
        var plan = SlotRegenerationPlan.Create(provider, now,
            [new(freshStart, freshStart + Duration.FromMinutes(30)), new(bookedStart, bookedStart + Duration.FromMinutes(30))],
            [new(Guid.NewGuid(), provider, bookedStart, "booked"), new(Guid.NewGuid(), provider, now + Duration.FromHours(1), "open"), new(otherOpenId, otherProvider, now + Duration.FromHours(1), "open")]);

        Assert.Single(plan.OpenSlotIdsToDelete);
        Assert.DoesNotContain(otherOpenId, plan.OpenSlotIdsToDelete);
        Assert.Equal([freshStart], plan.SlotsToInsert.Select(slot => slot.StartsAt));
    }

    private static WorkingHours Rule(DateOnly effectiveFrom, int weekday, string start, string end) =>
        new(Guid.NewGuid(), weekday, TimeOnly.Parse(start), TimeOnly.Parse(end), effectiveFrom, effectiveFrom);

    private static string[] UtcStarts(IEnumerable<GeneratedSlot> slots) => slots.Select(slot => slot.StartsAt.ToDateTimeUtc().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}
