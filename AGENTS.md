# Project agent memory

This file is the project's committed home for project-intrinsic agent knowledge: build, test, release, architecture, and sharp-edge notes that should travel with the code.

- Add durable project-specific notes here as they are discovered through real work.
- The root build and checks are documented in `README.md`; the architecture choices live in `PRD.md` and `ADR/`.
- Identity claim security and recovery behavior is implemented in `api/Identity/IdentityVerificationService.cs`; run the two root `scripts/check-*.sh` checks after changing it.

## Production and delivery

- The patient portal is `https://patientdataportal.adamroch.com` on Vercel; its API is `https://api-production-9d3d.up.railway.app` on Railway. Supabase owns authentication, PostgreSQL, and private storage; Resend sends from `portal@adamroch.com`.
- GitHub `origin` is the deployment source. GitLab remote `gitlab` is the Gauntlet submission mirror at `https://labs.gauntletai.com/adamroch/patient-data-portal.git`; pushing GitHub does not update GitLab automatically.
- Production enables the API outbox loop with `OUTBOX_BACKGROUND_ENABLED=true` and `OUTBOX_POLL_SECONDS=5`. The scheduled GitHub workflow is a fallback. The database lease and stable idempotency key make overlapping workers safe; see `ADR/0003-transactional-email-outbox.md` and `api/Email/EmailOutboxBackgroundService.cs`.
- Verify deployments using the live `/health` response and actual Railway instance logs, not only a green deployment badge. The container may print a non-fatal missing `libgssapi_krb5.so.2` message while database and storage checks remain healthy.

## Demo workflow

- Synthetic demo identities are listed in `docs/DEMO_PATIENTS.md`. Claims are permanent and one account can claim only one patient, so recheck `patient_records.claimed_by IS NULL` before handing an identity to a tester.
- `as-software-demo-edited.mp4` is a validated local artifact and intentionally ignored by Git; it must be copied separately if it should survive beyond Adam's workstation.
- Local web and API processes are unnecessary when testing the deployed application. Use them only for local development.

## Maintaining this file

Keep this file for knowledge useful to almost every future agent session in this project.
Do not repeat what the codebase already shows; point to the authoritative file or command instead.
Prefer rewriting or pruning existing entries over appending new ones.
When updating this file, preserve this bar for all agents and keep entries concise.
