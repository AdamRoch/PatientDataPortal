# ADR 0004: Serialize schedule mutations per provider

**Status:** Accepted

## Context

Availability regeneration, blocked-time edits, booking, cancellation, and rescheduling all mutate one provider's calendar. Row constraints prevent some duplicate inserts, but they do not by themselves stop regeneration and booking from observing incompatible intermediate states.

The brief also requires patients to select a provider and service.

## Decision

Every schedule mutation takes the same provider-scoped PostgreSQL advisory transaction lock. Operations for one provider serialize; operations for different providers remain concurrent.

V1 gives each provider one slot length and one or more offered services. Slots belong to the provider. The appointment records the selected service. Booking claims the slot and writes the appointment, requested and confirmed events, reminder work, and audit event in one transaction.

## Consequences

- The provider is the concurrency partition.
- No global scheduling lock is introduced.
- Availability edits can reliably reject collisions with booked appointments.
- Every scheduling test must use the same lock-taking production path.

## Rejected alternatives

- Depend only on a unique provider/start index. That does not serialize regeneration and availability edits.
- Give each service an independently bookable copy of a slot. Two services could then claim overlapping provider time.
