# Patient Data Portal

Patient imaging, reports, and scheduling portal. The monorepo contains an ASP.NET Core API (`api/`), a Next.js application (`web/`), and portable PostgreSQL migrations, seeders, benchmarks, and operations scripts (`infra/`).

## Quick start: a seeded local demo

This path needs a dedicated Supabase project because the seed creates private Storage objects and Supabase Auth users. It must contain **synthetic data only**. It is not a production setup and no command below has been run against your credentials by this README.

### 1. Install prerequisites

- [.NET SDK 10.0.400](global.json)
- Node.js 22 and npm
- A Supabase project with its session-pooler connection string, project URL, browser-safe anon key, and service-role key

For a deployed environment, domain, custom SMTP, and provider-account setup are human-owned steps in [INFRA-SETUP.md](INFRA-SETUP.md). Do not substitute a real patient or production database for the synthetic environment below.

### 2. Configure an ignored local environment

```sh
cp .env.example .env
cp .env.example web/.env.local
```

Edit both files. Replace every `replace-...` or `your-...` placeholder. Keep `NEXT_PUBLIC_SUPABASE_URL` and `NEXT_PUBLIC_SUPABASE_ANON_KEY` equal to their Supabase counterparts. Set `API_URL=http://localhost:5000` in `web/.env.local`.

Use a dedicated schema-owner connection for `MIGRATION_DATABASE_URL`, and the least-privileged session-pooler application role for `DATABASE_URL`. Generate unique high-entropy values for `APP_DB_PASSWORD`, `OUTBOX_JOB_SECRET`, `AUDIT_HMAC_KEY`, and `IDENTITY_HMAC_KEY`; set a local `DEMO_SEED_PASSWORD` of at least 12 characters. Keep `EMAIL_DELIVERY_MODE=log` locally. Never put a secret in this repository or set email delivery to `resend` unless an approved controlled delivery check is intended.

The API reads process environment variables. In the shell used for migration, seeding, tests, and the API process, export the root file:

```sh
set -a
. ./.env
set +a
```

### 3. Restore, migrate, and seed

```sh
dotnet restore PatientDataPortal.slnx
npm --prefix web ci

# Applies infra/migrations, creates/verifies APP_DB_ROLE, and checks constraints and least privilege through the application role.
dotnet run --project api -- --verify-migrations

# Idempotently creates synthetic patients, imaging, reports, and ten providers with 16,000 deterministic slots.
dotnet run --project api -- --seed-benchmark

# Creates pre-confirmed demo Auth accounts and links their database roles.
dotnet run --project api -- --seed-demo-accounts
```

`--seed-benchmark` needs `DATABASE_URL`, `SUPABASE_URL`, and `SUPABASE_SERVICE_KEY`; it creates private `study-assets` and `reports` buckets and refuses an imaging storage plan at or above the 1 GB free-tier budget. Re-runs use deterministic IDs and paths. It is therefore suitable only for a dedicated demo or benchmark environment, never data containing real patients or appointments.

### 4. Start the API and web app

In the first terminal, load the root environment as above and run:

```sh
ASPNETCORE_URLS=http://localhost:5000 dotnet run --project api
```

In a second terminal, run:

```sh
npm --prefix web run dev
```

Open <http://localhost:3000>. The API is at <http://localhost:5000>; its development OpenAPI document is available only while the API runs in the Development environment.

### Demo accounts

All four accounts use the value you chose for `DEMO_SEED_PASSWORD`; no password is committed here.

| Role | Email | Expected first view |
| --- | --- | --- |
| Patient, already claimed | `demo-patient@patient-data-portal.test` | Studies and signed reports for synthetic patient `SYN-0001` |
| Patient, unlinked | `demo-unlinked@patient-data-portal.test` | Identity-verification flow |
| Provider | `demo-provider@patient-data-portal.test` | Provider schedule and availability |
| Admin | `demo-admin@patient-data-portal.test` | Admin audit, outbox, and deletion-request views |

The demo seeder is idempotent and refuses to replace a patient claim held by a different account. It deliberately bypasses email confirmation only for these synthetic seed users; normal registration remains confirmation-gated.

## Validation

### Hermetic checks

These commands need no Supabase, Resend, deployed URL, or real credentials. They are the normal fresh-checkout validation suite; the API suite contains the unit and HTTP authorization tests, including scheduling idempotency/concurrency coverage and image, cine, report, and share denial/leakage coverage.

```sh
dotnet restore PatientDataPortal.slnx
dotnet build PatientDataPortal.slnx --no-restore
dotnet test tests/PatientDataPortal.Api.Tests/PatientDataPortal.Api.Tests.csproj --no-restore

npm --prefix web ci
npm --prefix web test
npm run lint
npm run typecheck
npm run build

./scripts/check-api-clock.sh
./scripts/check-identity-constant-time.sh
```

`check-api-clock.sh` prevents direct wall-clock use in API code so expiry tests remain deterministic. `check-identity-constant-time.sh` is a static security review gate for the unknown-reference dummy comparison; it is intentionally not a noisy statistical timing benchmark.

### Real local PostgreSQL coverage

The full database integration path requires a local `postgres:16-alpine` service (Docker is one option) and does not need Supabase or Resend. Use the same disposable settings as CI, then run migration verification and the API suite against that database:

```sh
docker run --rm --name patient-data-portal-postgres \
  -e POSTGRES_DB=patient_data_portal \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=postgres \
  -p 5432:5432 postgres:16-alpine
```

In another terminal:

```sh
export DATABASE_URL='Host=localhost;Port=5432;Database=patient_data_portal;Username=postgres;Password=postgres'
export MIGRATION_DATABASE_URL="$DATABASE_URL"
export APP_DB_ROLE=patient_data_portal_ci
export APP_DB_PASSWORD=patient_data_portal_ci_password
dotnet run --project api -- --verify-migrations
dotnet test tests/PatientDataPortal.Api.Tests/PatientDataPortal.Api.Tests.csproj --no-restore
```

The migration verifier proves both successful application-role access and expected duplicate, constraint, and forbidden audit-write failures. This is the database-backed validation path for the concurrency and leakage suites; it does not demonstrate a live Supabase deployment.

### Opt-in external contract checks

These checks contact third parties and require real deployed credentials. Load only an ignored local environment file for a dedicated test project. They are not hermetic and should never be presented as having passed unless you ran them and retained their output.

```sh
# Checks the Supabase session pooler and private buckets.
RUN_SUPABASE_CONTRACT_TESTS=true \
  dotnet test tests/PatientDataPortal.Api.Tests/PatientDataPortal.Api.Tests.csproj \
  --filter 'Category=Integration'

# Sends one generic, non-PHI message to a controlled inbox.
RESEND_API_KEY=... EMAIL_FROM=portal@example.com \
RESEND_TEST_RECIPIENT=controlled-inbox@example.com \
  dotnet test tests/PatientDataPortal.Api.Tests/PatientDataPortal.Api.Tests.csproj \
  --filter 'Category=Integration'
```

## k6 benchmark prerequisites and execution

k6 measures the API plus signed-Storage path. It is intentionally guarded: it will not send traffic until `ALLOW_BENCHMARK_TARGET=1` is set. Run it only against an approved, synthetic benchmark environment, never production or an environment containing real patient data. k6 results are measurements, not claims made by this document.

After migration and `--seed-benchmark`, create the deterministic booking fixture:

```sh
./infra/k6/prepare-booking-fixture.sh
```

Obtain a confirmed synthetic patient's access token and owned image/cine IDs. Then supply the API base URL and IDs (not the browser URL):

```sh
export ALLOW_BENCHMARK_TARGET=1 BASE_URL=https://approved-api.example
export PATIENT_ACCESS_TOKEN=replace-with-synthetic-patient-token
export IMAGE_ID=replace-with-owned-image-id CINE_ID=replace-with-owned-100-frame-cine-id
export PROVIDER_ID=replace-with-benchmark-provider-id RUN_ID=run-unique-id

k6 run infra/k6/image-load.js
k6 run infra/k6/cine-ttff.js
k6 run infra/k6/cine-full-load.js
k6 run infra/k6/slot-query.js
k6 run -e BOOKING_FIXTURE=infra/k6/artifacts/benchmark-k6-fixture.json infra/k6/booking.js
```

The scenarios allow only 20--50 virtual users and exactly 60 seconds. Use `VUS=50` only for an approved upper-bound run. Booking consumes slots; reset only the dedicated synthetic benchmark providers before another run:

```sh
./infra/k6/reset-and-reseed-bookings.sh
./infra/k6/prepare-booking-fixture.sh
```

The reset script deletes appointments, reminder-outbox rows, and appointment events only for the ten deterministic benchmark providers, then reopens their slots. It is not general cleanup. `cine-full-load.js` logs a conservative egress estimate: 100 frames × 40 KiB = 3.91 MiB per completed cine iteration, measured against Supabase's approximately 5 GiB monthly free-tier cap. Keep that log, the k6 p95 summary, target URL, commit SHA, synthetic fixture IDs, and any browser trace together.

k6 cannot establish when pixels paint or whether playback cadence drops frames. For an approved browser run, use Chrome DevTools on the deployed web app with Fast 3G (1.6 Mbps down, 750 Kbps up, 150 ms RTT), cache disabled, and a synthetic-patient session. Record a Performance trace with screenshots while a 100-frame cine loads and plays for at least ten seconds at 10, 12, and 15 FPS. Retain the trace and note first rendered frame, final readiness, and intervals longer than twice the selected frame interval. This complements k6; it does not turn local checks into deployed performance evidence.

## Architecture and product decisions

The browser uses Supabase only for email/password authentication. It sends the JWT to the ASP.NET Core API, which applies server-side role and ownership checks on every PHI route. The API uses Npgsql through the Supabase session pooler and a least-privileged database role for transactions. The Supabase service key is separate and limited to Auth administration and private Storage operations.

PostgreSQL holds metadata, provider slots, append-only audit events, share-token hashes, and the durable email outbox. Images, cine frames, and reports stay in private Supabase Storage. Authenticated patients receive short-lived signed Storage URLs after authorization. Public shares are different: the API proxies the bytes and revalidates the share on every request, allowing immediate revocation and an honest `shared_content_delivered` audit event.

Booking, schedule edits, audit events, and reminder intent use PostgreSQL transactions. Provider-scoped advisory locks serialize each provider's schedule mutations while allowing different providers to proceed concurrently. The outbox uses a lease and a stable Resend idempotency key because a database and email provider cannot commit atomically.

The complete accepted decisions, context, and rejected alternatives are in [ADR/](ADR/): transactional access (0001), public sharing (0002), email outbox (0003), schedule serialization (0004), identity recovery (0005), deletion and audit semantics (0006), hosting (0007), and email delivery (0008). Product decisions and the full data model are in [PRD.md](PRD.md).

## PHI, BAA, retention, and deletion

PHI includes patient identity and contact data, patient references and DOB, images/cine frames, report documents and metadata, appointments, share-recipient addresses, and access-scoped audit history. Application logs must contain no PHI: log only technical IDs and stable error codes, never names, DOBs, emails, references, report text, image metadata, or share tokens.

This is a demonstration project, not a representation that the deployment is HIPAA-ready. A real-world deployment would require a BAA with Supabase (Postgres, Auth, Storage), Railway (API hosting), Vercel (frontend delivery; no PHI is persisted there by design), and Resend (recipient addresses are disclosed while email bodies contain only a generic notice and secure link). Confirm each provider's current contractual eligibility with counsel before handling real PHI.

The retention/deletion behavior is defined by [ADR 0006](ADR/0006-audit-and-deletion-semantics.md). A patient can submit a deletion request; an administrator fulfills it using the approved, destructive procedure in [infra/scripts/README-deletion-fulfillment.md](infra/scripts/README-deletion-fulfillment.md). Fulfillment purges private Storage objects, revokes shares, removes share-email payloads and imaging/report metadata, anonymizes patient and appointment data, and removes the lookup between retained audit references and the patient. The append-only audit history remains, but cannot be re-linked through that lookup. Do not run the execute form during development, CI, or without approved deletion authorization.

## Hosting and service limits

The planned deployment uses Vercel's free tier for the web app, Railway Pro for the API, Supabase's free tier for Postgres/Auth/Storage, and Resend's free tier for email. Railway's paid plan does not sleep, but that is not proof of availability, redundancy, or failover. Vercel can have cold paths; Supabase can pause after approximately seven days of inactivity and has roughly 1 GB storage and 5 GB/month egress; Resend allows 100 emails/day and 3,000/month. A health probe is reachability evidence, not a resilience guarantee. Report local, external-contract, benchmark, and deployed results separately.
