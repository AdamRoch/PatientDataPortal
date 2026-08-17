# INFRA-SETUP — manual steps only a human can do

Everything agents need but cannot create themselves: accounts, purchases, DNS,
dashboard settings, and CLI logins. Do Part 1 completely before agents start on
E0. Part 2 is one short pass after the first deploy. Part 3 is test time.

Referenced by E0-T2 and E0-T7. Tickets consume these values; they never create them.

---

## Local demo runbook

Use the [seeded local-demo instructions in `README.md`](README.md#quick-start-a-seeded-local-demo) as the operational source of truth. This is a local API and web-app demo backed by a **dedicated Supabase project containing synthetic data only**; it is not a production environment.

Prerequisites are [.NET SDK 10.0.400](global.json), Node.js 22 with npm, and that dedicated Supabase project's session-pooler connection string, URL, anon key, and service-role key. Create the ignored local configuration files, replace every placeholder, and keep local email delivery in log mode:

```sh
cp .env.example .env
cp .env.example web/.env.local
# Edit both ignored files. In web/.env.local, set API_URL=http://localhost:5000.

set -a
. ./.env
set +a
```

The root `.env` and `web/.env.local` are ignored by Git. Do not commit them, paste their values into tickets, or use real patient data, production databases, or provider credentials. Set a local `DEMO_SEED_PASSWORD` with at least 12 characters; all seeded demo users use that value. The complete variable guidance, including the separate migration and application database connections, is in the [README's environment section](README.md#2-configure-an-ignored-local-environment).

Restore, build, migrate, and create the synthetic demo data:

```sh
dotnet restore PatientDataPortal.slnx
dotnet build PatientDataPortal.slnx --no-restore
npm --prefix web ci

dotnet run --project api -- --verify-migrations
dotnet run --project api -- --seed-benchmark
dotnet run --project api -- --seed-demo-accounts
```

Start the API in one terminal (after loading `.env`) and the web app in another:

```sh
ASPNETCORE_URLS=http://localhost:5000 dotnet run --project api
npm --prefix web run dev
```

Open the `Local` URL printed by the Next.js dev server (normally <http://localhost:3000>), rather than assuming a port is free. Sign in as `demo-patient@patient-data-portal.test` with `DEMO_SEED_PASSWORD` to see the pre-claimed synthetic patient; the [README demo-account table](README.md#demo-accounts) lists the provider, admin, and identity-verification accounts.

## Local, external-check, and deployment boundaries

Keep these three paths distinct when describing results:

| Path | What it is | Boundary |
| --- | --- | --- |
| Local synthetic development | The runbook above: local ASP.NET Core and Next.js processes, plus a dedicated synthetic Supabase project. | It supports development and demo work only. It does not establish cloud deployment, HIPAA readiness, availability, redundancy, or HA. |
| Optional external contract checks | The [opt-in Supabase and Resend checks](README.md#opt-in-external-contract-checks) contact third parties with a dedicated test environment and real credentials. | They are non-hermetic, must be run deliberately, and are not passed unless their output was retained. Never use real patient data. |
| Planned cloud deployment | The planned shape is Vercel for the web app, Railway for the API, Supabase for Postgres/Auth/private Storage, and Resend for email. | It has not been created or deployed by this document. Cloud credentials, account and billing ownership, domain/DNS, Resend verification, Supabase dashboard settings, and deployment-time configuration remain human-owned steps in Part 1 and Part 2 below. |

The architecture and hosting rationale are in [PRD.md](PRD.md) and [ADR 0007](ADR/0007-application-stack-and-demo-hosting.md); the verified-domain email decision is [ADR 0008](ADR/0008-email-delivery-on-free-tiers.md). Review the [README's PHI, BAA, retention, and deletion guidance](README.md#phi-baa-retention-and-deletion), [service-limit disclosures](README.md#hosting-and-service-limits), and [security hygiene sweep](README.md#security-hygiene-sweep) before sharing or deploying a change. This demonstration project makes no claim of HIPAA readiness, availability, failover, or high availability.

---

## Part 1 — Before agents start (blocking)

Do step 1 first: DNS propagation is the only step with a wait you can't control.

### 1. Domain + Resend (email)

- [ ] Pick a domain you own, or buy one (~$10/yr — Cloudflare, Porkbun, Namecheap).
      No website needed; only the DNS panel matters.
- [ ] Create a Resend account → **Domains → Add Domain** → enter the domain.
- [ ] Copy the DNS records Resend shows (SPF TXT + DKIM) into your registrar's DNS
      panel. Skip DMARC — optional, not needed here.
- [ ] Wait for Resend to show **Verified** (usually minutes, occasionally hours).
- [ ] Create a Resend **API key**. Note the SMTP settings while you're there:
      host `smtp.resend.com`, port `465`, username `resend`, password = the API key.
- [ ] Decide the sender address, e.g. `portal@<yourdomain>`.
- [ ] Have at least one inbox that is NOT your Resend login email (a second Gmail,
      work address, etc.) — needed to prove arbitrary-recipient delivery works.

### 2. Supabase

- [ ] Create a project. Pick a region and remember it (Railway should match —
      e.g. both US East). Save the database password when it's shown.
- [ ] Collect from the dashboard:
      - Project URL
      - anon/publishable key (web client)
      - service-role secret key (API only)
      - **Session pooler** connection string (Connect → Session pooler)
- [ ] **Authentication → Emails/SMTP settings:** enable custom SMTP and enter the
      Resend SMTP values from step 1, sender = your domain address. Leave email
      confirmations ON (default).
- [ ] **Authentication → Rate limits:** raise the email rate limit from the default
      (30/hour is fine for this project).
- [ ] Buckets: nothing to do — agents create `study-assets` and `reports`
      programmatically with the service key.

### 3. CLI logins (so agents can drive these non-interactively)

Run each in a terminal on this machine; they open a browser once and stay logged in:

- [ ] `railway login`  (Pro plan account)
- [ ] `vercel login`
- [ ] `gh auth status` — confirm logged in; if not, `gh auth login`
- [ ] Optional but useful: `supabase login` (lets agents use the Supabase CLI)

### 4. Hand over the secrets

- [ ] Create `SECRETS.local.env` in this directory with:

```
SUPABASE_URL=
SUPABASE_ANON_KEY=
SUPABASE_SERVICE_KEY=
DATABASE_URL=            # session pooler connection string
RESEND_API_KEY=
EMAIL_FROM=              # portal@<yourdomain>
```

Agents wire these into the real `.env` files, generate the remaining app secrets
(outbox job secret, HMAC key) themselves, keep this file out of git, and the
E8-T4 sweep double-checks nothing leaked.

---

## Part 2 — One pass after the first deploy (agents will tell you the URLs)

- [ ] Supabase **Authentication → URL Configuration:** set Site URL to the Vercel
      URL and add it to the redirect allow-list (the confirm-email link must land
      on the deployed app).
- [ ] Create a free UptimeRobot (or similar) monitor on the API's `/health`,
      5-minute interval. This is the uptime evidence AND keeps the Supabase
      free-tier project from pausing. Start it now so the history covers the
      eval window.

---

## Part 3 — Test time (yours, not the agents')

- [ ] Register on the deployed app with a fresh email → confirm the Supabase
      confirmation email arrives (via Resend).
- [ ] Share an image to the non-Resend inbox from Part 1 → confirm arrival.
- [ ] Run the heavy k6 cine benchmarks BEFORE the eval window opens (egress
      budget, E7-T2).
- [ ] Record the demo video in the E8-T7 mandated order, phone viewport included.

## Budget awareness

- Resend free tier: 100 emails/day, 3,000/month — shared by auth confirmations,
  shares, and reminders. CI/local use the mock sender, so real sends are rare.
- Supabase free tier: 1 GB storage (seed budgeted), ~5 GB/month egress (k6 cine
  runs budgeted in E7-T2), project pauses after ~7 days idle (probe prevents it).
- Railway Pro: already paid; API never sleeps.
