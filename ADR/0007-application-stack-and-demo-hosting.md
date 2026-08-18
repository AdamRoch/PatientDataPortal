# ADR 0007: Use an ASP.NET and Next.js monorepo with evidence-aware demo hosting

**Status:** Accepted

## Context

The assessment permits either C# or Node for the API and requires one reproducible submission. The deployment mixes an already-paid Railway plan (API) with free-tier services (Supabase, Vercel, Resend) whose pause, restart, usage, and egress behavior can affect measurements.

## Decision

Use a monorepo containing an ASP.NET Core API, a Next.js TypeScript web app, and shared infrastructure scripts. Deploy the web app to Vercel, the API to Railway on an existing paid Pro plan, and Postgres, Auth, and private Storage to Supabase. The brief names Railway as an allowed platform; the paid plan means the API never sleeps. Build the API from the repository's pinned multi-stage Dockerfile so local and Railway builds use the same .NET toolchain and runtime inputs.

External monitoring probes the health endpoint for uptime evidence; the probe also keeps the free-tier Supabase project from pausing for inactivity. The README discloses which components are paid (Railway) and which are free tier (Supabase, Vercel, Resend), and monitoring is not described as proof that the service cannot restart.

## Consequences

- One checkout owns migrations, seeds, tests, benchmarks, and grader instructions.
- The pinned Dockerfile, Railway configuration, and Docker ignore rules define the reproducible API build and exclude local secrets and unrelated web/test files from its context.
- C# transaction and lifecycle code benefits from compiler-checked types and framework conventions.
- The README must disclose every free-tier limitation that affects evidence.
- Local and deployed results are reported separately.

## Rejected alternatives

- Split the frontend and API into separate repositories. This adds coordination cost without helping the assessment.
- Render free tier for the API. Zero cost, but it sleeps after inactivity (cold starts pollute p95 evidence and demand a keep-warm probe) and has no native .NET runtime (requires a Dockerfile). It remains the documented fallback if the Railway plan lapses.
- Treat uptime monitoring as production resilience. It does not provide redundancy, failover, or a restart guarantee.
