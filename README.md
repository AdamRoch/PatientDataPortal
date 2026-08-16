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
```

See [PRD.md](PRD.md) and [ADR/0007-application-stack-and-demo-hosting.md](ADR/0007-application-stack-and-demo-hosting.md) for the agreed stack and hosting boundaries.

## Environment and service budgets

Copy `.env.example` to an ignored local environment file and fill every placeholder before
running an environment that calls Supabase or Resend. `DATABASE_URL` is the session-pooler
connection for the dedicated application database role; `SUPABASE_SERVICE_KEY` is reserved
for Auth administration and private Storage operations. Never use the service key as an
application database credential.

The Supabase buckets `study-assets` and `reports` are private. The Resend free tier permits
100 emails/day (3,000/month), shared by Auth confirmations, shares, and reminders. Supabase's
free tier includes roughly 1 GB storage and 5 GB/month egress; see `INFRA-SETUP.md` for the
human-owned domain, DNS, SMTP, and dashboard steps.
