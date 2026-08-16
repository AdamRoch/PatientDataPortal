# INFRA-SETUP — manual steps only a human can do

Everything agents need but cannot create themselves: accounts, purchases, DNS,
dashboard settings, and CLI logins. Do Part 1 completely before agents start on
E0. Part 2 is one short pass after the first deploy. Part 3 is test time.

Referenced by E0-T2 and E0-T7. Tickets consume these values; they never create them.

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
