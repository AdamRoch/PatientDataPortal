using NodaTime;

namespace PatientDataPortal.Api.Scheduling;

public sealed record ProviderSchedule(int SlotLengthMinutes, IReadOnlyList<WorkingHours> WorkingHours, IReadOnlyList<BlockedTime> BlockedTimes, IReadOnlyList<OfferedService> Services);
public sealed record WorkingHours(Guid Id, int Weekday, TimeOnly LocalStart, TimeOnly LocalEnd, DateOnly EffectiveFrom, DateOnly? EffectiveUntil);
public sealed record BlockedTime(Guid Id, Instant StartsAt, Instant EndsAt);
public sealed record OfferedService(Guid Id, string Name, bool Active);

public sealed record ReplaceWorkingHoursRequest(IReadOnlyList<WorkingHoursInput>? Rules);
public sealed record WorkingHoursInput(int Weekday, TimeOnly LocalStart, TimeOnly LocalEnd, DateOnly? EffectiveFrom, DateOnly? EffectiveUntil);
public sealed record UpdateSlotLengthRequest(int SlotLengthMinutes);
public sealed record BlockedTimeRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record CreateServiceRequest(string? Name, bool? Active);
public sealed record UpdateServiceRequest(string? Name, bool? Active);

public interface IProviderScheduleRepository
{
    Task<ProviderSchedule?> GetAsync(Guid userId, CancellationToken cancellationToken);
    Task<ProviderSchedule?> ReplaceWorkingHoursAsync(Guid userId, IReadOnlyList<WorkingHoursInput> rules, DateOnly today, CancellationToken cancellationToken);
    Task<ProviderSchedule?> UpdateSlotLengthAsync(Guid userId, int slotLengthMinutes, CancellationToken cancellationToken);
    Task<BlockedTime?> CreateBlockedTimeAsync(Guid userId, Instant startsAt, Instant endsAt, CancellationToken cancellationToken);
    Task<BlockedTime?> UpdateBlockedTimeAsync(Guid userId, Guid blockedTimeId, Instant startsAt, Instant endsAt, CancellationToken cancellationToken);
    Task<bool?> DeleteBlockedTimeAsync(Guid userId, Guid blockedTimeId, CancellationToken cancellationToken);
    Task<OfferedService?> CreateServiceAsync(Guid userId, string name, bool active, CancellationToken cancellationToken);
    Task<OfferedService?> UpdateServiceAsync(Guid userId, Guid serviceId, string name, bool active, CancellationToken cancellationToken);
    Task<bool?> DeleteServiceAsync(Guid userId, Guid serviceId, CancellationToken cancellationToken);
}
