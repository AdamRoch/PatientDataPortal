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

## Logging conventions

The API writes compact structured JSON logs. Every request has an `X-Request-Id` correlation
value, generated when absent and returned to the caller. Log fields may contain technical
identifiers such as request IDs, provider IDs, and error codes; they must never contain PHI:
names, dates of birth, email addresses, patient references, report text, image metadata, or
share tokens. Log only a stable error code for rejected domain requests, never user-supplied
or exception text.
