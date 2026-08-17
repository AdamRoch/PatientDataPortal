using NodaTime;

namespace PatientDataPortal.Api.Scheduling;

public static class SlotGenerator
{
    private const int RollingWindowDays = 56;

    public static IReadOnlyList<GeneratedSlot> Generate(Instant now, string timeZoneId, int slotLengthMinutes, IReadOnlyList<WorkingHours> rules, IReadOnlyList<BlockedTime> blockedTimes)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId)
            ?? throw new InvalidOperationException($"Provider has an invalid IANA time zone: {timeZoneId}.");
        var startDate = now.InZone(zone).Date;
        var endDate = startDate.PlusDays(RollingWindowDays);
        var slots = new List<GeneratedSlot>();

        for (var date = startDate; date < endDate; date = date.PlusDays(1))
        {
            var weekday = (int)date.DayOfWeek % 7;
            foreach (var rule in rules.Where(rule => rule.Weekday == weekday && AppliesOn(rule, date)))
            {
                var localStart = date.At(LocalTime.FromTicksSinceMidnight(rule.LocalStart.Ticks));
                var localEnd = date.At(LocalTime.FromTicksSinceMidnight(rule.LocalEnd.Ticks));
                for (var localSlotStart = localStart; localSlotStart.PlusMinutes(slotLengthMinutes) <= localEnd; localSlotStart = localSlotStart.PlusMinutes(slotLengthMinutes))
                {
                    var mapping = zone.MapLocal(localSlotStart);
                    if (mapping.Count == 0) continue;

                    // During fall-back, choose the earlier occurrence. This preserves one slot per
                    // authored local start time rather than creating a duplicate 01:00 slot.
                    var startsAt = mapping.First().ToInstant();
                    var endsAt = startsAt + Duration.FromMinutes(slotLengthMinutes);
                    if (startsAt <= now || blockedTimes.Any(block => startsAt < block.EndsAt && endsAt > block.StartsAt)) continue;
                    slots.Add(new(startsAt, endsAt));
                }
            }
        }

        return slots.OrderBy(slot => slot.StartsAt).ToArray();
    }

    private static bool AppliesOn(WorkingHours rule, LocalDate date)
    {
        var effectiveFrom = LocalDate.FromDateTime(rule.EffectiveFrom.ToDateTime(TimeOnly.MinValue));
        var effectiveUntil = rule.EffectiveUntil is { } value ? LocalDate.FromDateTime(value.ToDateTime(TimeOnly.MinValue)) : (LocalDate?)null;
        return date >= effectiveFrom && (effectiveUntil is null || date <= effectiveUntil.Value);
    }
}

public sealed record GeneratedSlot(Instant StartsAt, Instant EndsAt);

public sealed record PersistedSlot(Guid Id, Guid ProviderId, Instant StartsAt, string Status);

public sealed record SlotRegenerationPlan(IReadOnlyList<Guid> OpenSlotIdsToDelete, IReadOnlyList<GeneratedSlot> SlotsToInsert)
{
    public static SlotRegenerationPlan Create(Guid providerId, Instant now, IReadOnlyList<GeneratedSlot> generatedSlots, IReadOnlyList<PersistedSlot> existingSlots)
    {
        var scoped = existingSlots.Where(slot => slot.ProviderId == providerId).ToArray();
        var protectedStarts = scoped.Where(slot => slot.Status != "open").Select(slot => slot.StartsAt).ToHashSet();
        return new(
            scoped.Where(slot => slot.Status == "open" && slot.StartsAt >= now).Select(slot => slot.Id).ToArray(),
            generatedSlots.Where(slot => !protectedStarts.Contains(slot.StartsAt)).ToArray());
    }
}
