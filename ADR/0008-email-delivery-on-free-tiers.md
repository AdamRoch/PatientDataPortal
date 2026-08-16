# ADR 0008: Route all email through a verified Resend sending domain

**Status:** Accepted

## Context

Two graded flows depend on real email reaching addresses we do not control: a grader
registering on the deployed app must receive a Supabase confirmation email, and a grader
sharing an image or report must see the email arrive at a recipient address they typed.

Both free tiers restrict exactly these paths. Supabase's built-in SMTP is capped at a few
emails per hour and is documented as not for production use. Resend's free tier, without a
verified sending domain, can only deliver to the Resend account owner's own address. Every
local test, CI run, and demo recording sends to an address we control, so neither
restriction shows up before a grader hits the deployed app.

A separate wrinkle: the share email body is the share link itself, so a durable outbox row
(ADR 0003) must hold the plaintext URL until the send happens — which conflicts with the
original "token plaintext never persisted" rule.

## Decision

Verify a custom domain we own in Resend (SPF/DKIM DNS records) before any email feature
work begins. Configure Supabase Auth to use custom SMTP pointed at Resend, so confirmation
emails ride the same verified domain. Document the domain and SMTP variables in
`.env.example` and track the Resend free-tier daily budget (100 emails/day) alongside the
storage egress budget.

The share-token plaintext rule gains one bounded exception: the outbox payload carries the
full share URL until the send succeeds, and the worker scrubs it from the row on success.
The URL is never logged and never displayed pre-send (including in the admin outbox
viewer).

Dev mode and CI continue to use the log-instead-of-send sender; mocks never substitute for
real delivery on the deployed, graded paths.

## Consequences

- Requires owning a domain and DNS access; verification is external clock time, so it
  happens first, in E0-T2.
- Auth confirmations, share links, and reminders share one 100/day budget; CI and local
  runs must stay on the mock sender to protect it.
- Scrub-on-sent needs a test proving sent share rows retain no plaintext link.
- The README discloses the daily cap the same way it discloses egress limits.

## Rejected alternatives

- Mock or stub sends on the deployed app. The brief's accept lines require delivery via
  Resend; a stub is a failed accept line, not a mitigation.
- Ship on Supabase default SMTP. The hourly cap means a grader's confirmation email may
  simply never arrive, locking them out of every downstream flow.
- Disable email confirmation at signup. Verification is self-imposed (ADR 0005's
  anti-automation control), so this is the documented fallback if domain verification
  falls through — but it weakens the identity-claim story and is not the default.
- Keep the share URL out of the outbox by re-deriving it at send time. The token is
  random and only its hash is stored; there is nothing to derive it from.
