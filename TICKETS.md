# Ticket Backlog — Patient Imaging, Reports & Scheduling Portal

**Companion to `PRD.md`.** Ticket IDs: `E<epic>-T<n>`. Priorities are MoSCoW
(**Must / Should / Could**). "Depends on" lists hard blockers only.
**Estimation policy:** no timeline estimates or schedule sizing anywhere in this backlog;
sequence is expressed as dependencies only. The dependency-safe build order is:
**E0 → E1 → (E2 ∥ E5-schema) → E3 → E4 → E5 → E6 alongside everything → E7 → E8.**
Write the E6 adversarial tests **before or alongside** the features they attack — not after.

**Source of truth:** this file. The per-ticket files in `tickets/` are **generated** from it —
regenerate after any edit with `./split-tickets.sh` (never hand-edit `tickets/*.md`).

**Global Definition of Done (applies to every ticket):**
- Unit/integration tests for the ticket's logic; core-logic coverage counts toward ≥ 80%.
- Server-side validation and authorization on every new endpoint; denied attempts audited.
- No PHI in logs; errors are structured, non-500, user-safe.
- Patient-facing UI: usable at phone width; keyboard accessible; labeled controls.
- Migrations committed; seed script still reproducible from clean checkout.

---

## E0 — Foundation & DevEx (Must)

### E0-T1 Monorepo scaffold
- **Depends on:** —
- **Scope:** `/api` (ASP.NET Core Web API), `/web` (Next.js + TypeScript), `/infra` (migrations, seeds, k6, scripts). Lint + typecheck scripts for both apps; README stub.
- **Acceptance:** clean checkout → `dotnet build` and `npm run build` both succeed; lint scripts exist and pass on the scaffold.

### E0-T2 Supabase project + environment contract
- **Depends on:** E0-T1
- **Scope:** Supabase project (Postgres + Auth + private buckets `study-assets`, `reports`); Resend account; **two separated credentials (AD2): a least-privileged application DB role connecting via the session pooler (Npgsql connection string), and the Supabase server secret used only for Auth admin + Storage ops**; committed `.env.example` documenting **every** variable with placeholder values only. **Email delivery (ADR 0008):** verify a sending domain in Resend (SPF/DKIM DNS records — do this first, DNS propagation is external clock time) and point **Supabase Auth custom SMTP at Resend** (default Supabase SMTP is capped at a few emails/hour; Resend's free tier can't reach arbitrary recipients without a verified domain); domain + SMTP vars in `.env.example`; note the Resend daily budget (100/day free) alongside the egress budget. Human prerequisites (domain purchase, DNS records, account creation, dashboard SMTP config, CLI logins) live in `INFRA-SETUP.md` — this ticket consumes those values, it does not create them.
- **Acceptance:** `.env.example` is complete (a newcomer can fill it without reading code); the app DB role cannot touch Auth/Storage admin and the server secret is never used for general data access (spot-checked); no real secret anywhere in the repo (grep CI check optional but encouraged); a test email from the verified domain reaches an external address that is **not** the Resend account owner's; a fresh signup's confirmation email arrives via Resend SMTP.

### E0-T3 Database migrations + initial schema
- **Depends on:** E0-T2
- **Scope:** All tables/indexes/constraints from PRD §6: unique partial indexes on appointments (+ `service_id`, `schedule_version`), `UNIQUE(provider_id, start_at)` on slots, `services`, `verification_attempts`, unique share `token_hash`, `email_outbox` with `UNIQUE(appointment_id, schedule_version, interval)` for reminder rows (AD7), unique `patient_records.patient_ref` + unique `claimed_by`, `patient_claim_events` (claim/unlink/recovery history), `verification_attempts` storing **HMAC-scoped network/reference keys — never raw IPs**, `studies.visit_status (completed|scheduled|cancelled)` + `performed_at` + nullable `appointment_id` (the "completed visit" gate), `reports.patient_record_id` + `study_id`, FK from appointments protecting booked slots, audit_log append-only with **pseudonymous network/client references** (app role revoked UPDATE/DELETE; ADR 0006), and the **least-privileged app role grants themselves as migrations** (AD2). **Portable Postgres (PD11):** no FK or dependency on Supabase's `auth` schema — `user_profiles.user_id` is a soft reference — so the full schema migrates onto a plain Postgres container.
- **Acceptance:** `migrate` from empty DB produces the full schema **on both Supabase and a plain `postgres` Docker container**; constraints verified by failing negative inserts (e.g., second non-cancelled appointment on same slot is rejected by the DB).

### E0-T4 CI pipeline
- **Depends on:** E0-T1, E0-T3
- **Scope:** CI on every push: build, lint, unit tests for api + web; **a Postgres service container, migrated + seeded, so integration tests — including the E6 concurrency and leakage suites — run in CI against a real DB** (enabled by the portable schema, PD11).
- **Acceptance:** CI green on scaffold; fails on a deliberately broken commit; an integration test that touches the DB passes in CI (proving the service-container wiring before E6 needs it).

### E0-T5 Health endpoint
- **Depends on:** E0-T1
- **Scope:** `GET /health` reporting app, database, and storage reachability (each with status + latency).
- **Acceptance:** reachable unauthenticated; reports degraded (not 500) when a dependency is down; response contains no PHI or secrets.

### E0-T6 Structured logging baseline
- **Depends on:** E0-T1
- **Scope:** Request-ID correlation, structured JSON logs, documented field conventions ("identifiers, never PHI"), global exception middleware mapping domain errors → clean 4xx (never unhandled 500).
- **Acceptance:** a thrown domain error returns a clear non-500 body; logs show request ID and no PHI (spot-checked with a seeded patient name/DOB).

### E0-T7 Deploy skeletons + uptime probe
- **Depends on:** E0-T5, E0-T2
- **Scope:** Web → Vercel; API → **Railway (paid Pro plan — no sleep, native .NET build; AD8/ADR 0007)**; external monitoring probing `/health` every 5–10 min (uptime evidence, and — since `/health` touches the DB — keeps the Supabase free-tier project from pausing for inactivity); README discloses the hosting plans honestly (API host does not sleep on the paid plan; Supabase/Vercel free-tier behaviors named). Human-provided accounts, URLs, and CLI logins per `INFRA-SETUP.md`.
- **Acceptance:** public URLs for both apps; probe log/history exists; `/health` reachable publicly; README's hosting-plan disclosure present.

### E0-T8 Resend wrapper
- **Depends on:** E0-T2
- **Scope:** Server-side email sender abstraction with dev mode (log-instead-of-send), structured success/failure logging, retryable error taxonomy, and **pass-through of a caller-supplied stable idempotency key + capture of the provider message ID** (consumed by the outbox worker, E5-T8/AD7).
- **Acceptance:** test email sends from a local run; provider failure surfaces as a structured, retryable error — never an unhandled exception; idempotency key and message ID round-trip verified.

### E0-T9 Email outbox worker (AD7)
- **Depends on:** E0-T3, E0-T8
- **Scope:** The shared delivery engine for **all** outbound email (share links in E3, reminders in E5): `POST /api/jobs/email-outbox` (shared-secret header) claims `email_outbox` rows where `due_at <= now` and unsent, sends via the Resend wrapper with the row's stable idempotency key, records provider message ID + outcome, attempt cap with retry/backoff, stale-claim reclaim after crash; **on success, scrubs sensitive payload fields (the share URL) from the row (AD6/ADR 0008)**; external 15-min trigger (pg_cron or GitHub Actions) wired and documented. `due_at <= now` selection means a late/dropped tick can never lose work — the next tick catches up (PD10).
- **Acceptance:** two overlapping worker runs deliver each row exactly once (tested); crash between claim and send is recovered next tick without a duplicate email (idempotency key, tested); a simulated multi-tick outage delivers all due rows on the next run (tested); a sent share row retains no plaintext link (tested); every attempt logged, token/PHI-redacted.

### E0-T10 Injected clock seam
- **Depends on:** E0-T1
- **Scope:** All time reads go through NodaTime's `IClock` (already a dependency via PD6 — do not hand-roll one), registered in DI from the first service onward; a settable/advanceable `FakeClock` for tests. `DateTime.UtcNow` / `DateTimeOffset.Now` banned in `/api` by a lint/analyzer rule (or a CI grep) so the seam can't erode. This is the enabling seam for every time-dependent acceptance test: lockout expiry (E1-T4), minimum-notice (E5-T6), no-show-after-start (E5-T7), `due_at` selection and outage catch-up (E0-T9/E5-T8), share expiry (E3), DST generation (E5-T2), and E6-T5's "no timing luck" rule.
- **Acceptance:** the ban rule fails CI on a direct `DateTime.UtcNow` in `/api`; a sample test advances the fake clock past a boundary (e.g., 15-min lockout) and observes the behavior flip without sleeping.

---

## E1 — Auth & Identity Verification (Must)

### E1-T1 Auth UI (register / login / logout)
- **Depends on:** E0-T2
- **Scope:** Supabase Auth client wiring in `/web`: register (with **email verification** — Supabase confirm-email flow, clear "check your inbox" state), login, logout, session-expiry handling (redirect to login on expired session).
- **Acceptance:** new patient registers, confirms email, and logs in; unconfirmed account cannot reach protected resources; expired session is handled gracefully; an unauthenticated request to any patient resource is rejected (401).

### E1-T2 API JWT verification + role middleware
- **Depends on:** E0-T1, E0-T2
- **Scope:** Validate Supabase JWT server-side on every request; load role from `user_profiles`; deny-by-default authorization helpers used by all later endpoints.
- **Acceptance:** missing/invalid/expired token → 401; valid token with wrong role → 403; both audited where PHI-adjacent.

### E1-T3 Patient profile page
- **Depends on:** E1-T1
- **Scope:** View/edit basic profile (display name, time zone). Minimal by design (data minimization).
- **Acceptance:** patient can view/update own profile only; changes persist; validated server-side.

### E1-T4 Identity verification endpoint (AD5)
- **Depends on:** E1-T2, E0-T3
- **Scope:** `POST /api/identity/verify {patientRef, dob}` — requires an **email-verified** account (AD5). On match: link account→patient record (unique claim). Constant-time behavior (dummy compare when ref unknown); one generic error; every attempt persisted to `verification_attempts`; **durable account lockout** after 5 failures / 15 min with the same generic message; **network/patient-ref throttles** (stored as HMAC-scoped keys, never raw IPs — ADR 0005/0006) that slow automation but never hard-lock the record (no denial-of-service switch on a victim's record); audit every attempt (allowed/denied); claim, unlink, and recovery each write a `patient_claim_events` row. Includes the **audited admin recovery procedure** (endpoint or documented script) that unlinks a mistaken/compromised claim.
- **Acceptance (each covered by a test):**
  - Correct pair unlocks exactly that record's studies+reports; unconfirmed-email account cannot claim.
  - Wrong ref vs wrong DOB vs wrong both → identical error body and status code. (Constant-time behavior is enforced by implementation — dummy compare on the unknown-ref path — and verified by code review plus a non-CI timing script; **no timing-distribution assertion in CI**, that's a flake factory.)
  - 5th failure locks the account; lockout persists across API restart.
  - A distributed attack on one patient_ref from many accounts/IPs gets throttled (slowed), while the legitimate patient's own account is never locked by someone else's failures.
  - Already-claimed record → same generic error; no existence leak.
  - Admin unlink frees the record for a correct re-claim; the unlink is audited.
  - Verified flag gates `/api/studies`, `/api/images`, `/api/cine`, `/api/reports` (403 until verified).

### E1-T5 Verification UI flow
- **Depends on:** E1-T4, E1-T1
- **Scope:** Unlinked-state banner + verify form (ID + DOB) → success unlocks imaging/reports nav; locked state shows generic try-later.
- **Acceptance:** unverified users cannot reach imaging/report routes (client and server); flow fully usable at phone width; form fields labeled.

### E1-T6 Demo auth accounts seed
- **Depends on:** E0-T2, E0-T3
- **Scope:** Seed script step creating **login accounts** via the Supabase Auth admin API, **pre-confirmed** (email verification bypassed for seeds only, per F1) — this is separate from seeding `patient_records`, which are clinic rows, not logins: admin, provider accounts (linked to `providers` rows), **one pre-verified demo patient** (account already claimed to a patient record with images/reports, so graders see data instantly), and **one unlinked demo patient** (so the mandated demo video can show the verification flow live). Credentials listed in README (E8-T5).
- **Acceptance:** from a clean checkout, seeded credentials log in; the pre-verified patient sees studies/reports immediately; the unlinked patient hits the verify flow; provider/admin land on their role-appropriate views; idempotent re-run (no duplicate accounts).

---

## E2 — Imaging: viewing & cine (Must; Priority 1)

### E2-T1 Imaging seed generator
- **Depends on:** E0-T3
- **Scope:** Deterministic seeded RNG; ~50 patients × 1–5 completed visits × 1–10 images + 0–2 cine clips; **at least two 100-frame clips**; synthetic ultrasound-like frames at ~30–50 KB (budget total < 1 GB incl. thumbnails); upload to private bucket; metadata rows with `performed_at` + `visit_status`; **several patients also get `scheduled` and `cancelled` studies with images attached**, so the completed-visit gate has something real to hide.
- **Acceptance:** seed runs from clean checkout to the stated shape (counts logged); re-seed is deterministic; total storage usage reported and within free-tier budget; non-completed studies exist and are used by E2-T2's gate test.

### E2-T2 Studies list (API + UI)
- **Depends on:** E1-T4, E2-T1
- **Scope:** `GET /api/studies` — verified patient's own studies from **completed** visits only; UI list with study date/description; clean empty state.
- **Acceptance:** unverified → 403; another patient's study ID → 403/404 + audit; future/cancelled-visit studies never listed; empty state renders cleanly.

### E2-T3 Image viewer
- **Depends on:** E2-T2, AD3 mint endpoint
- **Scope:** `GET /api/images/{id}` → ownership check → metadata + signed URL (~5 min TTL). Viewer: zoom/pan via pointer events (mouse + touch), loading + error states, audit on view.
- **Acceptance:** only own images render; zoom/pan smooth at phone width; controls keyboard-accessible and labeled; audit row per view; expired signed URL re-mints on reload without error.

### E2-T4 Cine manifest + batched frame URLs
- **Depends on:** E2-T2, E0-T3
- **Scope:** `GET /api/cine/{id}` → ownership check → manifest (jsonb: ordered frames, default FPS, sizes) + `POST /api/cine/{id}/frame-urls` minting signed URLs in batches (TTL ~5 min).
- **Acceptance:** time-to-manifest is a single query + single round trip; batch minting returns ≤ N URLs per call; all access scoped and audited.

### E2-T5 Cine player
- **Depends on:** E2-T4
- **Scope:** Play/pause, previous/next frame, FPS control; default playback rate 10–15 FPS (stated in README); frame preloading with bounded concurrency; orientation change preserves playback state.
- **Acceptance:** a 100-frame clip plays at its default rate with no visible dropped frames on a normal connection; transport controls usable at phone width; scrubber/step buttons labeled.

### E2-T6 Corrupt/partial manifest degradation (edge case 2)
- **Depends on:** E2-T5
- **Scope:** Per-frame fetch failure → clear gap indicator at that position; playback continues across available frames; viewer never crashes.
- **Acceptance:** seeded clip with deliberately missing frame(s) shows gaps + message; automated test: viewer renders with a manifest referencing a nonexistent frame.

### E2-T7 Slow-network progressive load (edge case 3)
- **Depends on:** E2-T3, E2-T5
- **Scope:** Thumbnail/first-frame-first rendering, visible loading state, no UI freeze while remaining frames download.
- **Acceptance:** under browser throttling (documented preset), thumbnail/first frame paints quickly with a clear loading indicator; UI stays interactive throughout.

---

## E3 — Secure sharing (Must; Priority 1)

### E3-T1 Share mint endpoint
- **Depends on:** E2-T3, E0-T8
- **Scope:** `POST /api/share {resourceType: image|report, resourceId, recipientEmail}` — ownership check (reports: **signed only**, AD6); 128-bit CSPRNG token; store **SHA-256(token)** only; 48 h expiry; audit; **share email enqueued as an `email_outbox` row in the same transaction as the share row** (AD7) — the outbox payload carries the full share URL **transiently** (the only place plaintext lives at rest; the worker scrubs it after a successful send, E0-T9/ADR 0008). Returns the link.
- **Acceptance:** token plaintext never logged and never persisted **except transiently in the outbox payload until delivery** (scrub-on-sent tested in E0-T9; token-redacted logging verified); share row and outbox row commit or roll back together (tested); generation < 1.0 s p95 (server-log timing, recorded for benchmarks); duplicate shares of the same resource create independent links.

### E3-T2 Share email via Resend
- **Depends on:** E3-T1, E0-T9
- **Scope:** Outbox-driven send (the E0-T9 worker delivers share rows the same as reminders): generic body ("A medical image/report has been shared with you" + link + expiry window). No patient/study identifiers in body or subject; stable idempotency key per outbox row.
- **Acceptance:** email received via Resend in dev/prod; body contains zero PHI (checked against seeded patient data); send attempt logged with provider message ID; a retried/crashed send never produces a duplicate email (idempotency key, tested).

### E3-T3 Public share page
- **Depends on:** E3-T1
- **Scope:** `/s/{token}` — server validates **on every hit** (exists ∧ unexpired ∧ unrevoked) and renders the viewer (with single-file download button, PD4); expired/revoked → clear "no longer available" page; every delivery audited as `shared_content_delivered` (AD9); validation failures rate-limited. **Hardening (AD6):** `Referrer-Policy: no-referrer`, no third-party scripts on the public page, token-redacted logs. **Byte delivery (PD12):** content is streamed by `GET /api/public/share/{token}/content` — the API proxies the bytes from private storage with `Cache-Control: private, no-store`; no storage URL ever reaches the visitor, so revocation gates **every content request** and no Supabase cache header can leak revoked PHI. (Signed-URL offload stays for the authenticated portal, where the volume is.)
- **Acceptance:** expired and revoked links never serve content — including from browser cache (tested with back-button/reload **immediately after revocation** — the hard case); valid link renders at phone width; rate limiting kicks in on scripted guessing; content responses carry `private, no-store` and `no-referrer` (asserted in test).

### E3-T4 Share management (list + revoke)
- **Depends on:** E3-T1
- **Scope:** Patient portal: list own active/past links (resource, recipient, expiry, status); revoke action.
- **Acceptance:** revoked link dies immediately on next hit; patient sees only own links; revoke audited.

---

## E4 — Reports (Must; Priority 2)

### E4-T1 Reports schema + seed
- **Depends on:** E0-T3, E2-T1
- **Scope:** Reports as PDFs in private `reports` bucket; metadata rows; seed includes **both signed and preliminary** reports per several patients.
- **Acceptance:** seed produces both statuses; storage paths private (no public URL works).

### E4-T2 Reports list + viewer (signed-only)
- **Depends on:** E4-T1, E1-T4
- **Scope:** `GET /api/reports` and `GET /api/reports/{id}` — verified patient, own, `status='signed'` only; in-browser render via signed URL (embed) with correct formatting.
- **Acceptance:** preliminary reports never appear in any API response (tested); viewer renders correctly at phone width; views audited.

### E4-T3 Report sharing
- **Depends on:** E4-T2, E3
- **Scope:** Reuse E3 machinery with `resource_type='report'`; share page renders report.
- **Acceptance:** identical rules to image sharing (email, expiry, revoke, no-store); preliminary reports cannot be shared (rejected server-side, tested).

---

## E5 — Scheduling (Must; Priority 3)

### E5-T1 Availability rules + services CRUD (API + provider UI)
- **Depends on:** E1-T2, E0-T3
- **Scope:** Weekly working hours, slot length (one per provider in v1), blocked ranges, and the provider's **service list** (AD4 — name/active; the chosen service is stored on the appointment, not the slot) — create/read/update/delete, server-validated, provider-scoped (a provider edits only their own). Mutations take the provider-scoped advisory transaction lock (AD4).
- **Acceptance:** provider sets Mon–Fri 09:00–17:00 + 30-min slots + a blocked range + ≥1 service; all reflected downstream; past times never become bookable; UI usable at phone width.

### E5-T2 Slot generator (tz/DST-correct)
- **Depends on:** E5-T1
- **Scope:** NodaTime-based generation from rules+blocks into `slots` over a rolling window (8 weeks forward), in provider's IANA zone; regeneration on rule change (open slots only — never touch booked) runs inside the provider-scoped advisory transaction lock (AD4) so it can't race bookings; all `timestamptz`.
- **Acceptance:** for an overnight-hours provider in America/New_York, generation across both DST transition days yields correct UTC instants — no duplicated, skipped, or off-by-one-hour slots (automated test); generated list reflects hours+length+blocks exactly.

### E5-T3 Availability-edit collision guard (edge case 8)
- **Depends on:** E5-T2, E5-T5
- **Scope:** On rule/block change (inside the provider advisory lock, AD4), detect overlap with existing non-cancelled appointments → reject the edit with a clear message naming the conflict count; FK protects booked rows regardless.
- **Acceptance:** conflicting edit rejected, booked appointments untouched; non-conflicting edit succeeds and only genuinely free slots are removed.

### E5-T4 Open-slot discovery (API + patient UI)
- **Depends on:** E5-T2
- **Scope:** `GET /api/providers` (with each provider's services, AD4) + `GET /api/providers/{id}/slots?from&to` — only `status='open'` and future; patient picks **provider and service**, then a slot; picker grouped by day, times rendered in viewer's zone (labeled).
- **Acceptance:** booked/blocked/past slots never returned; query hits the slots index (EXPLAIN-checked); UI usable at phone width.

### E5-T5 Booking claim (atomic + idempotent)
- **Depends on:** E5-T4
- **Scope:** `POST /api/appointments {slotId, serviceId, idempotencyKey}` — duplicate key → return existing appointment; else one transaction holding the provider advisory lock (AD4): atomic `UPDATE slots … WHERE id=$1 AND status='open'`; 0 rows → 409 "slot no longer available"; success → insert appointment at `schedule_version=1` (`requested` → `confirmed` in the same transaction, both lifecycle events, PD2) + **reminder outbox row** (same transaction, AD7) + audit; confirmation response.
- **Acceptance:** booked slot immediately absent from open list; double-submit/retry with same key creates exactly one appointment; double-submit with different keys still yields one appointment (unique partial index backstop); serviceId validated as belonging to the provider; appointment + events + reminder row commit atomically (rollback tested); booking < 1.0 s p95 server-timing recorded.

### E5-T6 Reschedule & cancel
- **Depends on:** E5-T5
- **Scope:** Reschedule: claim new slot + free old slot in **one transaction** (provider advisory lock, AD4), updating `start_at` and **incrementing `schedule_version`**; cancel: free slot atomically; both enforce the 24 h minimum-notice rule server-side; patient-scoped (own appointments only). **Reminder interaction (AD7):** the same transaction supersedes the old version's pending reminder outbox row and inserts one for the new `(appointment_id, schedule_version, interval)`; cancel supersedes without replacement.
- **Acceptance:** freed slot reappears as bookable; changes < 24 h before start rejected with clear error; reschedule failure leaves the original appointment intact (transaction rollback tested); a rescheduled appointment's reminder fires for the **new** time and never the old one; a cancelled appointment is never reminded (both covered with E6-T5).

### E5-T7 Lifecycle state machine
- **Depends on:** E5-T5
- **Scope:** `requested → confirmed → completed | cancelled | no-show` (+ `requested → cancelled`); `requested → confirmed` happens automatically inside the booking transaction (PD2 — manual provider confirm is out of v1); `PATCH /api/appointments/{id}/status` covers the remaining role-appropriate, server-enforced transitions (provider/admin: complete, cancel, no-show); every transition → `appointment_events` + audit.
- **Acceptance:** cancelled↛completed; no-show only from confirmed and only after start time; invalid transitions rejected with clear 4xx (not silently ignored); patient cannot complete/no-show; both booking-time events (`requested`, `confirmed`) present on every appointment.

### E5-T8 Reminder scheduling (AD7/PD10)
- **Depends on:** E5-T5, E5-T6, E0-T9
- **Scope:** The reminder *scheduling* side of the outbox (delivery is E0-T9): booking inserts a reminder row with `due_at = start_at − 24 h`, keyed `UNIQUE(appointment_id, schedule_version, interval)`, in the booking transaction; reschedule/cancel supersede + re-insert per E5-T6; before send, the worker **re-checks the appointment is still confirmed and still at the row's schedule_version** (kills stale rows); generic body + portal link, no PHI (PD9). **Dev-mode env override for the lead interval** so a reminder can be triggered live in the demo video (PD10, E8-T7).
- **Acceptance:** an appointment booked > 24 h out gets exactly one reminder at the right time; rescheduled → reminded for the new time only; cancelled → never reminded; appointments booked < 24 h before start get no reminder (documented behavior); logs demonstrate ≥ 99% sent / 0 duplicates over a soak window.

### E5-T9 Provider schedule view + status actions
- **Depends on:** E5-T7
- **Scope:** Provider's own upcoming/past appointments (with service shown); complete / no-show / cancel actions (confirm is automatic, PD2); times in provider's zone.
- **Acceptance:** provider sees only own schedule; actions obey E5-T7 rules; usable at phone width.

### E5-T10 Patient appointments view
- **Depends on:** E5-T6
- **Scope:** Upcoming/past appointments with status (conveyed by more than color); entry points to reschedule/cancel.
- **Acceptance:** patient sees only own appointments; clean empty state ("no appointments yet"); phone-width usable.

### E5-T11 [Should] Email-outbox status viewer (admin)
- **Depends on:** E0-T9, E1-T2
- **Scope:** Read-only admin page over `email_outbox`: kind, status, attempts, `due_at`, sent-at, provider message ID. Share rows never display their payload URL (pre-send plaintext stays server-side, AD6/ADR 0008). Serves as demo evidence for the E8-T7 reminder moment and as the readable data behind the ≥ 99%-sent / 0-duplicates benchmark row (E7-T3).
- **Acceptance:** admin-only, enforced server-side (tested); a pending share row exposes no link; the reminder soak outcome is readable from the page.

---

## E6 — Adversarial & concurrency test suites (Must; built alongside features)

### E6-T1 Concurrent booking test
- **Depends on:** E5-T5
- **Scope:** Automated test (committed, CI-runnable, real DB): N simultaneous booking attempts on the last open slot.
- **Acceptance:** exactly one 2xx and exactly one persisted appointment; N−1 receive the clear conflict error; repeatable without flakes.

### E6-T2 Image/cine cross-patient leakage suite
- **Depends on:** E2-T3, E2-T4
- **Scope:** ID guessing/incrementing across studies/images/cine manifests and frame-URL minting; tampered/foreign JWTs; unverified-account attempts.
- **Acceptance:** every attempt rejected server-side (403/404, never bytes) **and** produces a denied audit row; suite runs in CI.

### E6-T3 Report leakage suite
- **Depends on:** E4-T2
- **Scope:** Mirror of E6-T2 for reports, including preliminary-report probing.
- **Acceptance:** same guarantees; preliminary never leaks.

### E6-T4 Share-link abuse suite
- **Depends on:** E3-T3
- **Scope:** Expired token, revoked token, foreign token, malformed token, scripted guessing (rate limit), cache revalidation after expiry.
- **Acceptance:** all yield "no longer available" (never content, never cached content); denials audited; rate limiter trips.

### E6-T5 Rules & idempotency suite
- **Depends on:** E1-T4, E5-T6, E5-T7, E5-T8, E0-T9
- **Scope:** Account-lockout persistence + throttle behavior (no record hard-lock); admin unlink recovery; booking double-submit (same + different keys); invalid lifecycle transitions; minimum-notice enforcement; outbox delivery under overlapping worker runs and crash-between-claim-and-send (no duplicate email, idempotency key); **catch-up after a simulated multi-tick outage; reschedule → reminder fires for new schedule_version only, never the old; cancel → no reminder**.
- **Acceptance:** each rule has a failing-then-passing automated test; no test relies on timing luck (clocks injected/mocked).

---

## E7 — Performance & benchmarks (Must; Stretch #16 = Could)

### E7-T1 Benchmark-scale seed
- **Depends on:** E2-T1, E5-T2
- **Scope:** Extend seed to the brief's stated dataset: 10 providers, ~16,000 slots, full imaging corpus.
- **Acceptance:** one command seeds the whole dataset deterministically; counts verified and logged.

### E7-T2 k6 scripts
- **Depends on:** E7-T1, deployed environment
- **Scope:** Committed k6 scripts: single image load, cine TTFF + full-load (100 frames), slot-availability query, booking action; 20–50 VU / 60 s profile. Plus a **browser-side timing harness** (Playwright trace or documented DevTools procedure) for what k6 can't see: first rendered frame, full cine readiness, and playback cadence/dropped frames (PRD §9). **Repeatability:** the booking script consumes slots — pair it with a committed reset/re-seed step so runs are repeatable. **Egress budget:** the cine full-load script moves real bytes from Supabase Storage (100 frames × ~40 KB × VUs × iterations); estimate and log egress per run against the free tier's ~5 GB/month cap, and schedule heavy benchmark iteration **before** the eval window opens so throttling can't corrupt live-demo p95s.
- **Acceptance:** scripts run against the deployed app and emit p95 summaries; booking script is repeatable via the reset step; README documents how to run them **and states the estimated egress per cine run**.

### E7-T3 Benchmark report
- **Depends on:** E7-T2
- **Scope:** Committed results table mapping every PRD §2 target → measured p95 + environment notes (hosting-plan disclosure per AD8: the API host does not sleep on the paid Railway plan; any residual cold paths — first request after deploy, Vercel function cold starts — reported honestly).
- **Acceptance:** every target row has a measured value; failures trigger fixes, not silence.

### E7-T4 [Could — Stretch #16, the only point-bearing bonus] Fast image/cine delivery
- **Depends on:** all Priority 1 Musts green, E7-T3 baseline
- **Scope:** Thumbnail-first progressive loading, prefetch/priority hints, parallel batched frame fetch, immutable caching with content-hashed keys.
- **Acceptance:** committed **before/after** benchmark demonstrating improvement over the core targets (especially the 100-frame cine case) + short technique write-up; **zero regressions** in any Priority 1 acceptance test. Explicit guard: started only when P1 core is fully green.

---

## E8 — Compliance, docs & demo (Must)

### E8-T1 Audit-log completeness pass
- **Depends on:** all feature epics
- **Scope:** Endpoint-by-endpoint checklist: every PHI read, every share mint/delivery, every denied attempt, every booking/status change → audit row with actor/action/target/timestamp, using the honest AD9 event names (`content_access_granted` for signed-URL issuance, `shared_content_delivered` for proxied share bytes — never claiming to observe fetches the API can't see).
- **Acceptance:** checklist committed; gaps fixed; append-only property verified at the DB level.

### E8-T2 Audit log viewer (admin + provider scope)
- **Depends on:** E8-T1
- **Scope:** Admin sees all entries (filterable by actor/action/date); provider sees entries touching own patients' data.
- **Acceptance:** scopes enforced server-side (tested); viewer usable; no PHI beyond references displayed.

### E8-T3 Retention & deletion
- **Depends on:** E2, E4, E5
- **Scope:** Written retention/deletion policy (images/cine/reports + appointment data); **patient-facing request path** (a "Request deletion of my data" action in the patient profile that records the request and surfaces it to admin — the brief says *patients can request* deletion, so the request entry point must be theirs); fulfillment: documented admin-run procedure/script that anonymizes DB rows and purges storage objects.
- **Acceptance:** policy in repo docs (naming each retained field and purpose, ADR 0006); a patient can submit a deletion request from their profile and admin sees it; deletion demonstrably removes a seeded patient's storage objects, anonymizes operational rows, disables their shares, and **removes the lookup linking retained audit references to the patient** (unlinkability tested); the retained audit history itself survives.

### E8-T4 Secrets & log hygiene sweep
- **Depends on:** all epics
- **Scope:** Repo-wide secret scan; log sweep against seeded PHI values (names, DOBs, emails); `.env.example` completeness re-check.
- **Acceptance:** zero findings; sweep method documented (repeatable command).

### E8-T5 README grader quick-start
- **Depends on:** all epics
- **Scope:** Install → configure from `.env.example` → migrate → seed → run → full test suite (unit, concurrency, leakage, k6) — executable in minutes; demo credentials for patient/provider/admin; architecture + AD/PD summary; BAA/PHI/retention statements.
- **Acceptance:** a fresh machine follows the README to a running seeded app and green test suite without asking questions.

### E8-T6 AI_USAGE.md
- **Depends on:** —
- **Scope:** Which AI tools were used and for what; runtime AI (none, unless NL-booking stretch is built — it is not in v1 scope); statement if no runtime AI used.
- **Acceptance:** file committed and accurate.

### E8-T7 Demo video (mandated order)
- **Depends on:** all Must epics
- **Scope:** Record, in the brief's exact order: identity verification (using the **unlinked** demo patient from E1-T6) → image/cine viewing → secure image/report sharing → report viewing → provider availability setup → patient booking → no-double-book behavior → reschedule/cancel → reminder sent/received (using the **dev-mode interval override** from E5-T8 — the 24 h default can't be demoed live); ≥ 1 phone-width segment.
- **Acceptance:** every mandated moment present, in order; phone viewport shown.

### E8-T8 Final verification pass
- **Depends on:** E7-T3, E8-T5, E8-T7
- **Scope:** Re-run benchmarks + uptime evidence near submission; confirm every brief *Accept* line maps to a passing test or demo moment.
- **Acceptance:** traceability checklist (brief line → test/demo) committed; nothing claimed without evidence.
