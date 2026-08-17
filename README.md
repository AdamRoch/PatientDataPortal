# Patient Data Portal

Monorepo for the patient imaging, reports, and scheduling portal.

- `api/` — ASP.NET Core Web API
- `web/` — Next.js TypeScript application
- `infra/` — future migrations, seeds, k6 scenarios, and operational scripts

## Local checks

Requires .NET SDK 10.0.400 and Node.js. From the repository root:

```sh
dotnet build
npm --prefix web ci
npm run build
npm run lint
npm run typecheck
./scripts/check-api-clock.sh
./scripts/check-identity-constant-time.sh
```

## Cine playback

The cine player defaults to the clip's manifest FPS when it is 10, 12, or 15 FPS; otherwise it defaults to 12 FPS. This keeps default playback within the intended 10–15 FPS range.

### Slow-network verification

Use Chrome DevTools' **Fast 3G** network preset (1.6 Mbps download, 750 Kbps upload, 150 ms RTT) when checking cine progressive loading. Open a 100-frame clip with the preset enabled: frame 1 should paint first, the player should show its loading status, and the transport controls should remain usable while later batches download.

All API code obtains the current time from NodaTime's injected `IClock`. The clock check is
also a CI gate; test code uses `NodaTime.Testing.FakeClock` so expiry behavior is deterministic.

`check-identity-constant-time.sh` is a deliberate code-review gate for the identity endpoint's
unknown-reference dummy comparison. It is not a statistical timing test in CI, because host
noise makes that kind of assertion flaky rather than meaningful.

The Supabase session-pooler and private-bucket test is deliberately opt-in: CI remains
hermetic and never receives production credentials. To run the external contract check after
loading an ignored local credentials file, use:

```sh
RUN_SUPABASE_CONTRACT_TESTS=true dotnet test tests/PatientDataPortal.Api.Tests/PatientDataPortal.Api.Tests.csproj --filter 'Category=Integration'
```

See [PRD.md](PRD.md) and [ADR/0007-application-stack-and-demo-hosting.md](ADR/0007-application-stack-and-demo-hosting.md) for the agreed stack and hosting boundaries.

## Environment and service budgets

## Hosting plan disclosure

The web app is deployed on Vercel's free tier. Vercel deployments and functions
can still have cold paths or be subject to free-tier limits, so deployed timing
evidence is reported separately from local results. The API is deployed on the
paid Railway Pro plan, which does not sleep and builds .NET natively; it is the
only paid hosting component in this deployment.

Supabase is on its free tier: it has roughly 1 GB of storage, about 5 GB/month
of egress, and can pause after roughly seven days without activity. Resend is
also on its free tier, with the delivery budget documented below. An external
probe calls the API's public `/health` route every 5–10 minutes for uptime
history and to exercise the database-backed health path. That probe is evidence
of observed reachability, not redundancy, failover, or a guarantee against a
service restart.

Copy `.env.example` to an ignored local environment file and fill every placeholder before
running an environment that calls Supabase or Resend. `DATABASE_URL` is the session-pooler
connection for the dedicated application database role; `SUPABASE_SERVICE_KEY` is reserved
for Auth administration and private Storage operations. Never use the service key as an
application database credential.

`IDENTITY_HMAC_KEY` is a distinct, high-entropy server secret used to pseudonymize identity
verification network and patient-reference throttle keys. It must never be exposed to clients.

Apply the schema with a schema-owner credential, then verify it through the application role:

```sh
dotnet run --project api -- --verify-migrations
```

This command uses `MIGRATION_DATABASE_URL` (or bootstrap `DATABASE_URL`) to apply files in
`infra/migrations/`, creates the deterministic least-privileged `APP_DB_ROLE`, and performs
negative constraint and least-privilege checks through that role. For Supabase pooler URIs,
the runner derives the pooler-decorated login from the URI parser; no credential is printed.
Do not use the migration credential at runtime.

Seed the complete synthetic benchmark dataset after applying the schema:

```sh
dotnet run --project api -- --seed-benchmark
```

The generator needs `DATABASE_URL`, `SUPABASE_URL`, and `SUPABASE_SERVICE_KEY`. It creates or
uses the private `study-assets` and `reports` buckets, then deterministically upserts 50 synthetic patient
records, completed studies, scheduled/cancelled studies, images, thumbnails, cine frames, and signed plus
preliminary synthetic PDF reports. It also creates 10 synthetic benchmark providers, each with 1,600 open
half-hour slots across a fixed 100-business-day window, for 16,000 slots total. The provider rows do not
create login accounts; the later demo-account command links its provider login to the first seeded provider.
The PDFs are explicitly non-clinical demo fixtures.

The command logs the imaging storage plan and verified imaging/provider/slot database counts. It refuses an imaging plan at
or above the 1 GB storage budget. Re-running it uses the same IDs and storage paths, so it does not add
seed rows or objects. Run it only against the dedicated demo or benchmark environment, never an environment
containing real patient or appointment data. These fixtures are synthetic only; this command seeds a dataset
and does not make a capacity or performance claim.

After benchmark data is seeded, create the deliberately pre-confirmed demo logins with a local
`DEMO_SEED_PASSWORD` (at least 12 characters):

```sh
dotnet run --project api -- --seed-demo-accounts
```

This command uses `SUPABASE_SERVICE_KEY` only for the Supabase Auth admin API, then writes
roles through `DATABASE_URL`. It creates one admin, one provider, a patient claimed to
`SYN-0001`, and an unlinked patient. It never changes the normal signup email-confirmation
flow, and it refuses to replace an existing patient claim. The demo credential list belongs
to the final quick-start documentation.

CI runs this same migrated Postgres verification against a disposable `postgres:16-alpine`
service, then runs the API test project with that database URL. Its verifier seeds a
representative appointment, reminder, and share-link fixture and deliberately attempts
duplicate and forbidden audit writes, so the job proves both successful database access and
expected constraint or privilege failures without Supabase or Resend credentials.

The Supabase buckets `study-assets` and `reports` are private. The Resend free tier permits
100 emails/day (3,000/month), shared by Auth confirmations, shares, and reminders. Supabase's
free tier includes roughly 1 GB storage and 5 GB/month egress; see `INFRA-SETUP.md` for the
human-owned domain, DNS, SMTP, and dashboard steps.

## Benchmark harness

The committed k6 scenarios live in `infra/k6/`. They measure the API-plus-signed-Storage
path for one image, cine time to first frame, a full 100-frame cine load, open-slot queries,
and appointment booking. Each scenario is intentionally fixed to the assessment profile of
20--50 VUs for 60 seconds and prints its p95. The target guard is deliberate: `k6` will not
send traffic until `ALLOW_BENCHMARK_TARGET=1` is set for an approved, synthetic benchmark
environment. Do not aim this harness at production or a system containing real patient data.

First migrate and seed that dedicated environment, then create a fixture containing the
deterministic benchmark provider, service, and slot IDs:

```sh
dotnet run --project api -- --seed-benchmark
./infra/k6/prepare-booking-fixture.sh
```

Set an access token for the seeded, confirmed synthetic patient and IDs from that patient's
seeded records. `BASE_URL` is the API base URL, not the browser URL. A 20 VU run looks like:

```sh
export ALLOW_BENCHMARK_TARGET=1 BASE_URL=https://approved-api.example
export PATIENT_ACCESS_TOKEN=replace-with-synthetic-patient-token
export IMAGE_ID=replace-with-owned-image-id CINE_ID=replace-with-owned-100-frame-cine-id
export PROVIDER_ID=replace-with-benchmark-provider-id RUN_ID=run-20260816-a
k6 run infra/k6/image-load.js
k6 run infra/k6/cine-ttff.js
k6 run infra/k6/cine-full-load.js
k6 run infra/k6/slot-query.js
k6 run -e BOOKING_FIXTURE=infra/k6/artifacts/benchmark-k6-fixture.json infra/k6/booking.js
```

Use `VUS=50` only when the approved run calls for the upper bound; `DURATION` must remain
`60s`. The booking run consumes slots. Before every rerun, reset the dedicated synthetic
benchmark environment and recreate its deterministic schedule:

```sh
./infra/k6/reset-and-reseed-bookings.sh
./infra/k6/prepare-booking-fixture.sh
```

The reset command deletes appointments, reminder-outbox rows, and appointment events only for
the ten deterministic benchmark providers, then reopens their slots. It is not a general data
cleanup command and must never be run against real patient data.

### Cine egress budget

`cine-full-load.js` logs `cine-egress-estimate` after every run. Its conservative model is
`completed iterations × 100 frames × 40 KiB`, or **3.91 MiB per full cine iteration**, compared
with Supabase's 5 GiB/month free-tier cap. For example, 20 VUs completing one cine each moves
about 78.13 MiB (1.53% of the cap); 50 concurrent first iterations move about 195.31 MiB
(3.81%). Count the logged figure across retries and run heavy cine benchmarks before the eval
window opens, so free-tier throttling cannot distort the demo measurements. `CINE_FRAME_BYTES`
and `CINE_FRAME_COUNT` can be set when the actual fixture differs, but the report must retain
the values used.

### Browser timing trace

k6 cannot establish when pixels appear or whether playback misses its cadence. For each
approved browser run, use Chrome DevTools against the deployed web app and retain the exported
Performance trace (`.json.gz`) with the k6 summary:

1. Sign in as the synthetic patient, open an owned 100-frame cine clip, and open DevTools.
2. In Network, select **Fast 3G** (1.6 Mbps down, 750 Kbps up, 150 ms RTT), disable cache, and
   preserve the network log. Never use a real-patient session.
3. In Performance, enable screenshots, click Record, reload the cine page, wait until the UI
   shows every frame is ready, play for at least ten seconds at 10, 12, and 15 FPS, then stop
   and export the trace.
4. Record first rendered frame from the first screenshot containing the cine image, full cine
   readiness from the final frame-storage request/load event, and playback cadence by comparing
   successive rendered-frame timestamps to the selected FPS interval (100, 83.3, or 66.7 ms).
   Count intervals materially longer than twice the selected interval as dropped frames.

Keep the URL, commit SHA, synthetic fixture IDs, throttling profile, trace filename, p95 summary,
and the k6 egress line together. This is the browser-side evidence for PRD §9; it complements,
but does not replace, the protocol-level k6 result.

At this branch's creation, the public deployment is E0-only. These scripts have only static and
local validation until current main deploys the full image, cine, and scheduling product. Do not
claim deployed acceptance, p95 compliance, or a Supabase egress observation from this change
alone.

Email delivery defaults to `EMAIL_DELIVERY_MODE=log`, which records only the provider-safe
result and the idempotency key. The narrow Resend integration test is opt-in and sends one
generic, non-PHI delivery check only when all three environment values are supplied:

```sh
RESEND_API_KEY=... EMAIL_FROM=portal@example.com RESEND_TEST_RECIPIENT=controlled-inbox@example.com \
  dotnet test tests/PatientDataPortal.Api.Tests/PatientDataPortal.Api.Tests.csproj --filter 'Category=Integration'
```

## Email outbox trigger

`POST /api/jobs/email-outbox` delivers due outbox rows. It requires the
`X-Outbox-Job-Secret` header, whose value is `OUTBOX_JOB_SECRET`. The endpoint uses a short
database lease and Resend's stable idempotency key, so concurrent or retried ticks do not
create a second provider command. Each successful send clears the stored payload, including a
share URL. Retryable failures are rescheduled with exponential backoff; terminal failures stay
visible as `failed` with `due_at = infinity`.

`.github/workflows/email-outbox.yml` invokes this endpoint every 15 minutes. Configure the
repository secrets `OUTBOX_JOB_URL` (the deployed API base URL, without a trailing slash) and
`OUTBOX_JOB_SECRET` (the same server-side value). GitHub Actions scheduling can run late; that
is safe because the worker always selects every unsent row whose `due_at` is in the past.

Appointment reminders are scheduled only when an appointment starts after the configured
lead interval (24 hours by default); appointments booked inside that interval do not receive
a catch-up reminder. In Development only, set `REMINDER_LEAD_MINUTES` to a positive value to
use a shorter lead time for the demo. Production ignores that override. Reminder emails contain
only a generic portal notification and a link to the portal, never appointment details.

## Logging conventions

The API writes compact structured JSON logs. Every request has an `X-Request-Id` correlation
value, generated when absent and returned to the caller. Log fields may contain technical
identifiers such as request IDs, provider IDs, and error codes; they must never contain PHI:
names, dates of birth, email addresses, patient references, report text, image metadata, or
share tokens. Log only a stable error code for rejected domain requests, never user-supplied
or exception text.
