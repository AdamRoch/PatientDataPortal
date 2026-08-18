# Patient Imaging, Reports & Scheduling Portal — PRD

**Status:** Draft v2 · **Last updated:** 2026-08-15
**Source of truth for scope:** `AS-Software-ProjectBrief.md` (take-home assessment brief). Where this PRD and the brief conflict, the brief wins.
**Estimation policy:** this document and `TICKETS.md` contain **no timeline estimates or schedule sizing**. Work is ordered by dependency and priority only.

---

## 1. Overview

Patients who undergo an ultrasound exam need fast, secure, self-service access to their
own images — including cine clips — and their finalized report, plus the ability to share
either via a secure, time-limited link. The portal also provides scheduling (book,
reschedule, cancel; provider availability management), which is **fully required but
secondary**.

**Priority order (from the brief, rubric-weighted):**

1. **Priority 1 — Image access & secure sharing** (primary focus)
2. **Priority 2 — Report/document delivery & sharing**
3. **Priority 3 — Scheduling** (correctness-critical: no double-booking under concurrency)
4. **Cross-cutting — PHI security/compliance** (weighted double the scheduling track;
   treated as first-class throughout, not a phase)

---

## 2. Goals & success measures

**Product goals**

- G1: A verified patient can view their own images and cine clips, and only their own.
- G2: A patient can share one image or one report via a time-limited, revocable,
  unguessable link delivered by email. Public shared bytes are served through the API
  so revocation is enforced on every content request.
- G3: A verified patient can view only signed/finalized reports.
- G4: A patient can book/reschedule/cancel; a provider can manage availability;
  no double-booking is possible under concurrency.
- G5: Provable PHI hygiene: server-side authorization on every PHI endpoint, append-only
  audit log, no PHI in logs, documented retention/deletion.

**Performance targets (graded benchmarks, measured with k6 against the seeded dataset:
~50 patients with 1–5 completed visits each, 1–10 images and 0–2 cine clips per visit,
clips up to 100 frames; 10 providers; ~16,000 slots; 20–50 concurrent VUs for 60 s)**

| Target | Value |
|---|---|
| Single image load | < 1.0 s p95 |
| Cine time-to-first-frame (100-frame clip) | < 1.0 s p95 |
| Cine fully loaded & smoothly playable (100 frames) | < 5.0 s p95 |
| Share-link generation | < 1.0 s p95 |
| Slot-availability query | < 1.0 s p95 |
| Booking action | < 1.0 s p95 |
| No double-booking under concurrency | 1 success / N−1 rejected |
| Reminder dispatch | ≥ 99% of due reminders sent, 0 duplicates |
| Uptime (deployed demo) | ≥ 99% over the eval window |

---

## 3. Non-goals (v1)

- Native mobile app (responsive/mobile-first web only; PWA-installable is a Could).
- SMS delivery (Twilio) — optional in the brief; email satisfies all requirements.
- Real DICOM ingestion — mock/synthetic image data only.
- Cine-clip sharing and whole-study sharing — shares are single-image or single-report only.
- Provider UI for viewing patient images/reports.
- Stretch items without rubric points: waitlist/auto-fill (#17), recurring appointments (#18),
  insurance/intake capture (#19), natural-language booking (#20).
- Stretch #16 (fast image/cine delivery, +5 bonus): **in scope as a Could, gated on all
  Priority 1 core acceptance criteria passing first** — it can never displace core work.

---

## 4. Users & roles

Three roles, enforced **server-side on every request** (never trusted from the client).

| Capability | Patient | Provider | Admin (front desk) |
|---|:---:|:---:|:---:|
| Register / verify email / log in | ✅ | seeded account | seeded account |
| Verify identity (Patient ID + DOB) and unlock imaging/reports | ✅ | — | — |
| View own images / cine / signed reports | ✅ (own only) | — | — |
| Share own image/report via secure link; revoke own links | ✅ | — | — |
| Book / reschedule / cancel own appointments | ✅ | — | — |
| Manage own availability (hours, slot length, blocks) | — | ✅ (own) | — |
| View schedule; complete / cancel / no-show transitions (confirm is automatic, PD2) | — | ✅ (own) | ✅ |
| Review audit log | — | ✅ (entries touching own patients' data) | ✅ (all) |

**PHI inventory (graded "HIPAA awareness" artifact — stated in README):** patient identity
(name, DOB, patient reference, contact details), ultrasound images and cine frames, report
documents and their metadata, appointments (patient ↔ named provider ↔ time), share-link
recipient email addresses, and the audit log itself (sensitive; access-scoped). App logs
must contain **no PHI** — identifiers/references only.

**BAA disclosure (README):** Supabase (Postgres + Auth + Storage), Railway (API host),
Vercel (frontend host — serves UI; no PHI persisted), Resend (email — message bodies carry
no PHI by design; recipient addresses disclosed). Each would require a BAA for real-world use.

---

## 5. Locked decisions

### Architecture decisions (AD)

Detailed context, rejected alternatives, and consequences are recorded in `ADR/`.

| # | Decision | Rationale (short) |
|---|---|---|
| AD1 | **Backend: C# / ASP.NET Core Web API.** Frontend: React/Next.js (TypeScript). | Batteries-included framework (validation, DI, logging, config, health checks) minimizes decision tax and maximizes convention coherence, which is graded; compiler-enforced service layer for the correctness-critical core. (Documented alternative: Node/NestJS.) |
| AD2 | **Transactional data access uses Npgsql through the Supabase session pooler with a dedicated least-privileged application database role.** The Supabase server secret is separate and used only for Auth administration and private Storage operations. Explicit server-side ownership checks are mandatory; narrow RLS policies may add defense in depth. | Booking, rescheduling, audit writes, and outbox insertion require real multi-statement PostgreSQL transactions. Separating database and Supabase API credentials limits each credential's blast radius. |
| AD3 | **Authenticated first-party image/report viewing uses short-lived Storage signed URLs after server authorization. Public share delivery is proxied through the API and revalidates the share token on every content request.** Relevant responses use `Cache-Control: private, no-store`. | A Storage signed URL cannot be recalled after issuance. Proxying the small single-image/report public-share path makes immediate revocation and per-delivery auditing truthful while keeping cine traffic off the API host. |
| AD4 | **Materialized provider-slot model with explicit services.** A provider has one slot length in v1 and may offer multiple services. Slots belong to the provider; the chosen service is stored on the appointment. Every schedule mutation takes the same provider-scoped PostgreSQL advisory transaction lock. Booking claims the slot and inserts the appointment, events, and audit row in one transaction. | The brief requires provider/service selection. Provider-scoped serialization prevents regeneration, availability edits, booking, and rescheduling from racing without creating a global bottleneck. |
| AD5 | **Identity verification is an email-verified account→patient-record claim** using Patient ID + DOB. Responses are generic. Account lockout is durable; IP and patient-reference throttles slow automation but do not hard-lock the patient record. An audited admin recovery procedure can unlink a mistaken or compromised claim. | DOB is a weak secret. Email verification and recovery prevent a first-claimer from becoming an irreversible takeover, while avoiding a record-wide denial-of-service switch. |
| AD6 | **Share tokens:** 128-bit CSPRNG, URL-safe; **SHA-256 hash stored, never plaintext** — with one deliberate exception: the outbox payload carries the share URL transiently until delivery, then it is scrubbed (never logged); scoped to exactly one image OR one signed report; 48 h expiry; revocable; per-delivery audit; token-redacted logs; `Referrer-Policy: no-referrer`; no third-party scripts on the public page; public endpoint rate-limited; email contains no PHI. | Prevents database disclosure from exposing live links and limits leakage through logs, referrers, analytics, and guessing. The email body *is* the link, so a durable outbox (AD7) must hold it until send; scrub-on-sent bounds the exposure window. API-proxied content makes revocation effective on the next request. |
| AD7 | **Email is a transactional-outbox workflow.** Share creation and reminder scheduling insert an `email_outbox` row in the same database transaction as the triggering state. A worker claims due rows, calls Resend with a stable idempotency key, and records the provider message ID and outcome. Reminder identity includes appointment schedule version and interval. | PostgreSQL and Resend cannot commit atomically. A durable outbox plus provider idempotency handles retries and crash-after-send ambiguity without pretending a database claim row alone provides exactly-once delivery. |
| AD8 | **Monorepo** (`/api`, `/web`, `/infra`); deploy: Vercel (web) + **Railway (api — existing paid Pro plan, built from the repository's pinned multi-stage Dockerfile)** + Supabase + Resend. External monitoring probes `GET /health` (uptime evidence; also keeps the free-tier Supabase project active). Hosting plans disclosed honestly in the README, including which components are paid vs free tier. | The brief permits Railway by name; an already-paid always-on host removes API sleep as a benchmark variable, while the Dockerfile makes the API build reproducible across local and Railway environments. Honest evidence boundaries remain for restarts and the free-tier components (Supabase, Vercel, Resend limits). |
| AD9 | **Audit events describe what the application can prove.** For authenticated signed-URL delivery, the event is `content_access_granted`; for API-proxied public shares, the event is `shared_content_delivered`. Audit rows are append-only and retain only policy-approved pseudonymous references. | The API cannot observe every later fetch of a Storage signed URL. Naming the observed event avoids claiming evidence the system does not possess. |

### Product decisions (PD)

| # | Decision |
|---|---|
| PD1 | Verification gates **imaging and reports only**. Booking is open to any registered user (they are creating their own data). |
| PD2 | Booking records `requested` and immediately transitions to `confirmed` in the same transaction, producing both lifecycle events. Provider manual-confirm configuration is outside v1. |
| PD3 | Availability edits that collide with an already-booked slot are **rejected with a clear message** (no silent delete/flag-and-keep). Booked slots are additionally protected by FK. |
| PD4 | Share scope = **one image or one report**. Share page renders in-browser view and offers a single-file download. Cine sharing: not in v1. |
| PD5 | Provider UI scope: availability CRUD, own schedule, status transitions, audit entries for own patients. **No provider image/report viewing in v1.** |
| PD6 | All times persisted as `timestamptz` (UTC). Provider availability authored in the provider's IANA time zone; slot generation via a real tz library (**NodaTime**); every UI renders times in the viewer's local zone with the zone labeled. |
| PD7 | Auth via **Supabase Auth client SDK** (email/password); the API validates the Supabase JWT server-side. Passwords never transit our API. |
| PD8 | Cine **manifest stored in DB** (`jsonb`: ordered frames, default FPS, byte sizes); frames in private storage; API mints **batched** signed frame URLs. |
| PD9 | All email bodies (share + reminder) are generic notices + secure link — **no PHI in email**. |
| PD10 | Reminder interval: **24 h before start**; each appointment schedule version owns a durable reminder row with `due_at`. The worker selects unsent work where `due_at <= now`, so a late trigger cannot lose work. A dev-mode interval override supports the demo. Defaults: account lockout = 5 failed attempts / 15 min; share expiry = 48 h. |
| PD11 | **Schema is portable Postgres.** No FK into Supabase's `auth` schema — `user_profiles.user_id` is a soft reference to `auth.users.id`. Migrations + integration/concurrency tests run against a plain Postgres container in CI; Supabase specifics (Auth, Storage, pg_cron) are integration points, not schema dependencies. |
| PD12 | **Public share bytes are proxied through the API** (`GET /api/public/share/{token}/content` streams the bytes with `Cache-Control: private, no-store`), never handed off as a Storage URL. Authenticated patient-portal viewers keep direct short-TTL signed URLs (AD3), where cine volume makes offload valuable. |
| PD13 | **All outbound email — including Supabase Auth confirmation emails — sends through a verified Resend sending domain** (ADR 0008). Supabase Auth uses custom SMTP pointed at Resend: the default Supabase SMTP is capped at a few emails/hour, and Resend's free tier cannot reach arbitrary recipients without a verified domain — both would break graded flows on the deployed app. The Resend daily budget (100/day free) is tracked alongside the egress budget. |

---

## 6. System architecture

### Topology

```
Browser (Next.js on Vercel)
  │  Supabase JS: auth only (register/login/session)
  │  HTTPS + Bearer JWT: all API calls
  ▼
ASP.NET Core API (Railway)
  ├── Npgsql, session pooler, least-privileged app DB role ──► Postgres (Supabase):
  │     metadata, slots, audit, share hashes, durable outbox (AD2/AD7)
  ├── Supabase server secret (Auth admin + Storage ops ONLY, never general data access)
  ├── mint signed URLs ──► Supabase Storage (private buckets: study-assets, reports)
  ├── email_outbox worker ──► Resend API: share + reminder emails (generic body + link,
  │     stable idempotency key per row, AD7)
  └── /api/jobs/email-outbox ◄── external cron tick (pg_cron or GH Actions), shared secret

Authenticated portal traffic fetches image/report BYTES directly from Supabase Storage
via short-lived signed URLs. Public SHARE bytes are the one exception: streamed through
the API (`/api/public/share/{token}/content`) so revocation gates every request (PD12).
```

### Data model (metadata in Postgres; binaries in Storage — never raw frames in DB)

- **patient_records** — seeded clinic records: `patient_ref` (the "Patient/Account ID", unique), `dob`, `full_name`, `claimed_by → user` (unique; nullable — admin recovery can unlink, AD5).
- **verification_attempts** — one row per attempt: `account_id`, HMAC-scoped network/reference keys, `result`, `attempted_at` — backs durable account lockout and automation throttles without retaining raw network/reference values. **patient_claim_events** record claim, unlink, and recovery actions.
- **user_profiles** — mirrors Supabase `auth.users` (`user_id` is a **soft reference** — no FK into the `auth` schema, per PD11): `role (patient|provider|admin)`, `display_name`, `tz`.
- **providers** — `user_id`, `tz`, `slot_length_min` (one per provider in v1).
- **services** — `provider_id`, `name`, `active` (a provider offers one or more services, AD4; the chosen service lives on the appointment, not the slot).
- **availability_rules** (`provider_id`, weekday, local start/end, effective range) · **blocked_times** (`provider_id`, `tstzrange`).
- **slots** — `UNIQUE(provider_id, start_at)`; `status (open|booked|blocked)`; generated rows.
- **appointments** — `slot_id`, `patient_user_id`, `provider_id`, `service_id` (AD4), `start_at` (denormalized for the constraint), `schedule_version` (incremented on every reschedule — reminder identity, AD7/PD10), `status (requested|confirmed|completed|cancelled|no-show)`, `idempotency_key`; partial unique indexes: `(provider_id, start_at) WHERE status NOT IN ('cancelled')` and `(slot_id) WHERE status NOT IN ('cancelled')`; `UNIQUE(patient_user_id, idempotency_key)`.
- **appointment_events** — every transition: from/to status, actor, role, timestamp.
- **studies** — `patient_record_id`, `appointment_id` (nullable for imported history), `performed_at`, `visit_status (completed|scheduled|cancelled)`, description. **images** and **cine_clips** each reference one study; only completed studies are exposed.
- **reports** — `patient_record_id`, `study_id`, `status (preliminary|signed)`, `signed_at`, `signed_by`, `storage_path` (PDF). Patient access requires both ownership and `status='signed'`.
- **share_links** — `token_hash` (unique), `resource_type (image|report)`, `resource_id`, `recipient_email`, `expires_at`, `revoked_at`, `access_count`, `last_accessed_at`.
- **email_outbox** — transactional outbox (AD7): `kind (share|reminder)`, payload refs (a share row's payload carries the full share URL transiently until delivery, then scrubbed — AD6), `due_at`, `status (pending|claimed|sent|failed)`, `attempts`, `idempotency_key` (stable, sent to Resend), `provider_message_id`, timestamps; reminder rows are `UNIQUE(appointment_id, schedule_version, interval)` and inserted/superseded **in the same transaction** as booking/reschedule/cancel; worker selects unsent rows where `due_at <= now` (a late tick can never lose work, PD10).
- **audit_log** — append-only (application role revoked UPDATE/DELETE): actor, role, action, target type/id, **result (allowed|denied)**, pseudonymous network/client references, timestamp. Retention and pseudonymization are explicit.

**Storage layout (private buckets):**
`study-assets/studies/{study_id}/images/{image_id}.jpg` · `…/thumbnails/{image_id}.jpg` ·
`…/cine/{clip_id}/f{0001}.jpg` · `reports/{report_id}.pdf`

### Key request flows

- **View own image:** client → `GET /api/images/{id}` → API checks JWT + verified ownership chain + completed visit → audit `content_access_granted` → returns metadata + signed URL (TTL ~5 min) → browser fetches bytes from Storage.
- **View via share link:** visitor → `GET /api/public/share/{token}/content` → hash(token) lookup → exists ∧ unexpired ∧ unrevoked? → no: "no longer available" (+ denied audit); yes: API streams the object with `private, no-store` and audits `shared_content_delivered`. The token is redacted from logs and never sent as a referrer.
- **Book:** client → `POST /api/appointments {slotId, serviceId, idempotencyKey}` → duplicate key? return existing appointment → else, inside one transaction holding the provider-scoped advisory lock (AD4): atomic claim UPDATE (0 rows → 409 "slot no longer available") + insert appointment (`requested` → `confirmed`, both events, PD2) + reminder outbox row + audit → confirmation.
- **Send email:** the feature transaction inserts `email_outbox` with a stable key → worker claims due row with a lease → calls Resend using the same idempotency key on every retry → records provider message ID/outcome. Unknown outcomes remain inspectable rather than being guessed successful.

---

## 7. Functional requirements

Acceptance criteria below condense the brief; the brief's own *Accept* lines are the
binding text. Edge cases (§8) are requirements, not nice-to-haves.

### Foundation
- **F1 Registration & auth** — email/password via Supabase Auth with **email
  verification at signup** (Supabase confirm-email flow; unconfirmed accounts can't
  reach protected resources); sessions expire; any unauthenticated request to a
  protected resource → 401. Passwords hashed by Supabase (never plaintext, never
  through our API). Seeded demo accounts are created pre-confirmed via the admin API.

### Priority 1 — Image access & secure sharing
- **F2 Identity verification (AD5/PD1)** — ID+DOB match against seeded record links the
  **email-verified** account and unlocks that patient's studies **and reports only**.
  Generic error; constant-time; **durable account lockout** after repeated failures plus
  IP/patient-ref throttles (never a record hard-lock); unique claim; audited admin
  recovery procedure can unlink a mistaken/compromised claim.
- **F3 View my images** — only own, only from **completed** visits; viewer with zoom/pan;
  fully usable at phone width.
- **F4 Cine playback** — manifest-driven viewer: play/pause, prev/next frame, FPS control;
  default 10–15 FPS (stated in README); 100-frame clip plays without visible dropped frames.
- **F5 Share an image (AD6/AD7)** — mint + durable email outbox; revocable from portal;
  expiry 48 h; public content is API-proxied and reauthorized on every request;
  expired/revoked → clear "no longer available", never the content.
- **F6 No cross-patient image access** — adversarial automated test: ID
  guessing/incrementing, foreign/expired share tokens → all rejected server-side + audited.

### Priority 2 — Reports
- **F7 View my reports** — only **signed/finalized**; preliminary never exposed by any API
  response; in-browser rendering with correct formatting.
- **F8 Share a report** — identical mechanism/rules to F5; preliminary reports cannot be shared.
- **F9 No cross-patient report access** — same adversarial rigor as F6.

### Priority 3 — Scheduling
- **F10 Availability management** — provider service offerings, weekly hours, one v1 slot
  length, and blocked ranges; generated open slots reflect the schedule; blocked/past never
  bookable; collision-with-booking edits rejected (PD3); every mutation takes the provider lock.
- **F11 Slot discovery & booking** — patient picks a **provider and service** (AD4), sees
  only genuinely open future slots; booking persists, confirms, and immediately removes
  the slot from the open list.
- **F12 No double-booking under concurrency (AD4)** — N simultaneous claims on the last
  slot → exactly one succeeds; verified by a committed automated concurrent test at the
  DB level.
- **F13 Reschedule & cancel** — provider-locked atomic slot swap; minimum-notice rule
  enforced server-side; freed slots become bookable; reschedule increments `schedule_version`,
  supersedes obsolete reminder work, and creates the replacement reminder in the transaction.
- **F14 Status lifecycle (PD2)** — `requested → confirmed → completed / cancelled /
  no-show`; only valid, role-appropriate transitions; invalid rejected server-side;
  every transition recorded.
- **F15 Email reminders (AD7/PD10)** — durable reminder work becomes due 24 h before start;
  the outbox worker catches up after late ticks and calls Resend with a stable
  appointment/schedule-version idempotency key; every attempt and outcome is recorded.

### Cross-cutting
- **F16 Audit log (AD9)** — every PHI access grant, public shared-content delivery, denied
  attempt, share mint/revoke, claim/recovery event, and booking/status change; append-only;
  reviewable by admin (and provider for own patients); event names match what the system observes.
- **F17 Health & observability** — `GET /health` reports app/DB/storage reachability, returning
  200 only when every dependency is healthy and 503 with the same non-secret status body otherwise;
  structured error logging with request IDs and no PHI; no unhandled 500s in demo flows.
- **F18 Retention & deletion** — documented policy covering images/cine/reports and
  appointment data; patient deletion request honored (anonymize + storage purge).

---

## 8. Edge cases & failure modes (all required; mapped to work)

| # | Edge case | Owning requirement → planned vertical slice |
|---|---|---|
| 1 | Identity mismatch: generic error, no partial hints, account lockout without record-wide denial of service | F2 → identity claim/recovery slice |
| 2 | Corrupted/partial cine manifest: graceful gaps, no crash | F4 → cine slice |
| 3 | Slow-network progressive load: quick first paint, clear loading state | F3/F4 → image and cine slices |
| 4 | Small-viewport/touch: viewer, scrubber, share controls usable; orientation change preserves playback | F3/F4/F5 → image, cine, and sharing slices |
| 5 | Expired/revoked share reuse: clear message, no content; public bytes never escape through a reusable Storage URL | F5/F8 → sharing slices |
| 6 | Time zones & DST: unambiguous per-viewer display; single correct instant; DST-safe slot generation; UTC persistence | F10 → provider availability slice |
| 7 | Concurrent booking on last slot: exactly one confirmed | F12 → atomic booking slice |
| 8 | Availability edit colliding with booked slot or concurrent booking: rejected, booking preserved | F10/F12 → availability and booking slices sharing the provider lock |
| 9 | Reminder idempotency under repeated, overlapping, late, and crash-ambiguous runs | F15 → durable reminder slice |
| 10 | Double-submit of booking: at most one appointment (server-side dedupe) | F11 → atomic booking slice |
| 11 | Cancel/no-show rules enforced; cancel frees slot; reschedule replaces reminder identity | F13/F14/F15 → appointment-management slice |
| 12 | Dependency failure, validation, and clean empty states | F17 → every vertical slice plus final adversarial verification |

---

## 9. Non-functional requirements

- **Security:** TLS in transit (platform defaults on Vercel/Railway/Supabase); encryption at
  rest (Supabase defaults) — documented in README; server-side authz on every PHI route;
  secrets in env vars only; committed `.env.example` with placeholders; no PHI in logs.
- **Performance:** §2 targets under protocol-level k6 load plus a browser harness for
  first rendered frame, full cine readiness, and playback cadence. Authenticated signed-URL
  delivery keeps cine traffic off the API; public single-file shares favor revocation
  correctness over bandwidth optimization. DB indexes back slot queries, token hashes,
  outbox claims, and audit writes. **Egress budget:** Supabase free-tier bandwidth is a real
  constraint on cine benchmarking — each full-cine k6 run moves hundreds of MB, so runs
  are counted against an estimated per-run egress figure, heavy benchmark iteration
  happens **before** the eval window opens, and the k6 booking script includes a
  reset/re-seed step so runs are repeatable without exhausting slots.
- **Accessibility:** keyboard-navigable flows; labeled controls (incl. slot and cine
  buttons); contrast; status conveyed by more than color.
- **Responsive/mobile-first:** every patient-facing flow usable at phone width.
- **Reliability:** hosting behavior reported honestly (AD8) — the API host does not sleep
  on the paid Railway plan; free-tier components' limits (Supabase pausing/egress, Vercel
  function cold starts, Resend daily cap) are named in the README; an external `/health`
  probe provides uptime evidence and keeps the Supabase project active; dependency
  failures degrade with clear user-facing messages + structured logs.
- **Testing/quality:** ≥ 80% coverage on core logic (identity verification, imaging/report
  access control, booking engine + concurrency guard, reschedule/cancel rules, lifecycle
  transitions, reminder + share scheduling); committed k6 scripts; Playwright E2E for the
  golden path incl. a phone-viewport run; CI runs lint + unit (+ E2E) on every push;
  schema via committed migrations, reproducible from clean checkout with a seed script;
  every benchmark target has an explicit evidence owner before implementation begins.

---

## 10. Deliverables (submission)

- GitHub repo: README + `AI_USAGE.md` + `.env.example` + committed seed script (patients
  with images/cine/reports incl. **both signed and preliminary** reports, 10 providers,
  ~16,000 slots, demo patient/provider/admin accounts).
- Deployed app URL with ≥ 99% uptime evidence over the eval window.
- README grader quick-start: install → configure → seed → run → full test suite
  (concurrency, leakage, benchmarks) in minutes, demo credentials listed.
- Video demo **in the brief's mandated order**: identity verification → image/cine viewing →
  secure image/report sharing → report viewing → provider availability setup → patient
  booking → no-double-book behavior → reschedule/cancel → a reminder sent/received —
  plus at least one phone-width viewport segment.

## 11. Open questions (non-blocking)

- PWA-installable manifest (Could) — include only if all Must/Should work is green.
- Stretch #16 scope is deliberately open-ended; commit before/after benchmark + write-up
  only if every Priority 1 core criterion passes.
