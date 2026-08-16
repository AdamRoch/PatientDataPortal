# ADR 0003: Deliver email through a transactional outbox

**Status:** Accepted

## Context

PostgreSQL and Resend cannot participate in one atomic transaction. A worker can crash after Resend accepts an email but before the database records success. A database claim row alone therefore cannot guarantee zero duplicate external effects.

## Decision

The transaction that creates a share or schedules a reminder also inserts an `email_outbox` row. A worker leases due rows, calls Resend using the row's stable idempotency key, and records the provider message ID and outcome.

Reminder identity includes appointment ID, schedule version, and interval. Rescheduling supersedes old pending reminder work and creates replacement work in the reschedule transaction. The worker selects every unsent row where `due_at <= now`, so delayed triggers catch up.

## Consequences

- Feature state cannot commit without its durable email intent.
- Retries repeat the same provider command instead of creating a new send.
- Ambiguous outcomes stay visible and recoverable.
- Operational evidence must distinguish queued, claimed, accepted, delivered when known, failed, and ambiguous.

## Rejected alternatives

- Send inline after committing the feature row. A crash or provider failure loses the notification path.
- Claim, send, then mark without provider idempotency. A crash after acceptance can duplicate the email.
