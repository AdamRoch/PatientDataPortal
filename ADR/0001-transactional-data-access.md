# ADR 0001: Use PostgreSQL connections for transactional data access

**Status:** Accepted

## Context

Booking, rescheduling, lifecycle events, audit rows, and email-outbox rows must commit together. A Supabase server key can call Supabase APIs and bypasses RLS, but it is not a substitute for an explicit PostgreSQL transaction boundary.

## Decision

The ASP.NET API uses Npgsql through the Supabase session pooler and a dedicated least-privileged application database role. The Supabase server secret is held separately and used only for Auth administration and private Storage operations.

Every PHI endpoint applies explicit server-side ownership checks. Narrow RLS policies may be added as defense in depth, but the API does not depend on an RLS-bypassing credential for ordinary data access.

## Consequences

- Multi-table invariants can commit or roll back as one unit.
- Database and Supabase API credentials have separate, smaller blast radii.
- Migrations must create and test the application role's grants.
- The connection mode and pool limits must be documented and benchmarked.

## Rejected alternatives

- Use the Supabase service role for all data access. This grants more authority than the API needs and obscures transaction boundaries.
- Make several independent Data API calls and compensate on failure. This cannot protect booking and outbox invariants.
