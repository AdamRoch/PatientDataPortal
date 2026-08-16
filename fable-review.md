 Real issues, ranked

  1. The reminder due-window design silently fails your own ≥99% target. PD10/E5-T8 define "due" as start_at ∈ [now+24h ± half-tick]. That's a
  point-in-time window, which means if the external cron misses even a couple of ticks (GitHub Actions schedules are notoriously delayed/dropped;
  Render can be asleep when the tick lands), every appointment whose window passed during the outage is never reminded — the next run doesn't
  look backward. Your stale-claim reclaim only rescues claimed-but-unsent rows, not never-claimed ones. The fix is cheaper than the bug: define
  due as start_at BETWEEN now AND now + 24h AND no send record exists. The UNIQUE(appointment_id, interval) claim-by-insert you already designed
  makes the wide query safe, and you get catch-up semantics for free. This one directly threatens the "≥99% sent" benchmark row.

  2. Reschedule × reminder interaction is unhandled. E5-T6 reschedules by moving the same appointment to a new slot. E5-T8 keys idempotency on
  UNIQUE(appointment_id, interval). So: patient books for Friday, reminder sends Thursday, patient reschedules to next Wednesday → the send
  record already exists → no reminder ever fires for the new time. And the inverse: a reminder already claimed for the old time goes out
  referencing a moved appointment. Neither the PRD nor E5-T6/T8/E6-T5 mentions this. Options: key the send record on (appointment_id, start_at),
  or delete/invalidate pending send records inside the reschedule transaction. Either way, add it to E6-T5's test list — it's exactly the kind of
  cross-feature seam a grader pokes.

  3. Supabase free-tier egress is an unbudgeted resource for the k6 benchmarks. You budgeted storage (<1 GB, good) but not bandwidth. AD3 routes
  all bytes through Supabase Storage, and E7-T2 runs cine full-load (100 frames × 30–50 KB ≈ 3–5 MB per clip) at 20–50 VUs for 60 s. A handful of
  benchmark runs plus iteration during E7-T4 tuning can chew through the free tier's monthly egress cap (5 GB on the free plan), after which
  Supabase throttles and your p95 numbers — and the live demo during the eval window — degrade for a reason that has nothing to do with your
  code. Worth an explicit line in E7: estimate egress per run, cap benchmark repetitions, and consider running the heavy cine benchmark before
  the eval window opens. Related smaller point: the k6 booking script consumes slots, so E7-T2 needs a reset/re-seed step to be repeatable — not
  currently stated.

  4. Nobody owns seeding the auth accounts. The deliverables require committed demo patient/provider/admin accounts, but E2-T1 seeds
  patient_records (clinic records, not logins) and E7-T1 seeds providers/slots (rows, not logins). Creating Supabase Auth users programmatically
  needs the admin API and is its own small chunk of work — including deciding which demo patient account is pre-verified (so images show
  instantly) versus left unlinked (so the video can demo the verification flow, which the mandated demo order requires first). Add it to a seed
  ticket explicitly.

  5. CI's test database strategy conflicts with your schema. user_profiles mirrors auth.users, which presumably means an FK into Supabase's auth
  schema. E6-T1 requires a "real DB" concurrency test in CI, but E0-T4 doesn't say what that DB is. Plain Postgres in a CI service container
  won't have auth.users; supabase start in CI works but is a heavier setup. Decide now (I'd lean supabase CLI in CI, or drop the FK and treat
  user_profiles.user_id as a soft reference) — this is the kind of thing that ambushes you on day 2 of 3.

  6. Cache-Control: no-store on the share page doesn't control the image bytes. E3-T3's acceptance ("revoked links never serve content, including
  from browser cache, tested with back-button") is the right test, but the mechanism is incomplete: the browser fetches the actual bytes
  directly from Supabase Storage via the signed URL, and Supabase's response headers govern that cache entry, not your page's. Short TTL mostly
  saves you, but back-button within the TTL after a revoke can replay bytes from memory cache. Since you already wrote the honest test, just be
  aware the fix may need to live on the storage side (or accept and document the residual within-TTL window — defensible, since revocation
  "effective on next hit" is what AD6 promises anyway).
  pre-verified (so images show instantly) versus left unlinked (so the video can demo the verification flow, which the mandated demo
  order requires first). Add it to a seed ticket explicitly.

  5. CI's test database strategy conflicts with your schema. user_profiles mirrors auth.users, which presumably means an FK into        Supabase's auth schema. E6-T1 requires a "real DB" concurrency test in CI, but E0-T4 doesn't say what that DB is. Plain Postgres in
  a CI service container won't have auth.users; supabase start in CI works but is a heavier setup. Decide now (I'd lean supabase CLI    in CI, or drop the FK and treat user_profiles.user_id as a soft reference) — this is the kind of thing that ambushes you on day 2 of
  3.
                                                                                                                                        6. Cache-Control: no-store on the share page doesn't control the image bytes. E3-T3's acceptance ("revoked links never serve
  content, including from browser cache, tested with back-button") is the right test, but the mechanism is incomplete: the browser
  fetches the actual bytes directly from Supabase Storage via the signed URL, and Supabase's response headers govern that cache entry,  not your page's. Short TTL mostly saves you, but back-button within the TTL after a revoke can replay bytes from memory cache.
  Since you already wrote the honest test, just be aware the fix may need to live on the storage side (or accept and  5. CI's test database strategy conflicts with your schema. user_profiles mirrors auth.users, which presumably       means an FK into Supabase's auth schema. E6-T1 requires a "real DB" concurrency test in CI, but E0-T4 doesn't say
  what that DB is. Plain Postgres in a CI service container won't have auth.users; supabase start in CI works but is
  a heavier setup. Decide now (I'd lean supabase CLI in CI, or drop the FK and treat user_profiles.user_id as a       soft reference) — this is the kind of thing that ambushes you on day 2 of 3.                                                                                                                                                            6. Cache-Control: no-store on the share page doesn't control the image bytes. E3-T3's acceptance ("revoked links
  never serve content, including from browser cache, tested with back-button") is the right test, but the mechanism
  is incomplete: the browser fetches the actual bytes directly from Supabase Storage via the signed URL, and          Supabase's response headers govern that cache entry, not your page's. Short TTL mostly saves you, but back-button   within the TTL after a revoke can replay bytes from memory cache. Since you already wrote the honest test, just be
  aware the fix may need to live on the storage side (or accept and document the residual within-TTL window —         defensible, since revocation "effective on next hit" is what AD6 promises anyway).                                  Smaller notes
                                                                                                                      - How is "completed visit" modeled? Core #3 gates images on completed visits, but the §6 data model shows studies   → images with no visit/status field and no link to appointments. The seed talks about "completed visits" but the
  schema never defines one. One status or visit_date+status field on studies closes it — just make it explicit
  before E0-T3.                                                                                                       - The 24 h reminder can't be demoed. E8-T7 must show a reminder sent/received, but PD10 hardcodes 24 h before
  ("revoked links never serve content, including from browser cache, tested with back-button") is the  right test, but the mechanism is incomplete: the browser fetches the actual bytes directly from
  Supabase Storage via the signed URL, and Supabase's response headers govern that cache entry, not    your page's. Short TTL mostly saves you, but back-button within the TTL after a revoke can replay    bytes from memory cache. Since you already wrote the honest test, just be aware the fi  4. Nobody owns seeding the auth accounts. The deliverables require committed demo patient/provider/admin accounts, but E2-T1 seeds
  patient_records (clinic records, not logins) and E7-T1 seeds providers/slots (rows, not logins). Creating Supabase Auth users programmatically   needs the admin API and is its own small chunk of work — including deciding which demo patient account is pre-verified (so images show
  instantly) versus left unlinked (so the video can demo the verification flow, which the mandated demo order requires first). Add it to a seed
  ticket explicitly.
                                                                                                                               5. CI's test database strategy conflicts with your schema. user_profiles mirrors auth.users, which presumably means an FK into Supabase's auth
  schema. E6-T1 requires a "real DB" concurrency test in CI, but E0-T4 doesn't say what that DB is. Plain Postgres in a CI service container       won't have auth.users; supabase start in CI works but is a heavier setup. Decide now (I'd lean supabase CLI in CI, or drop the FK and treat      user_profiles.user_id as a soft reference) — this is the kind of thing that ambushes you on day 2 of 3.
                                          6. Cache-Control: no-store on the share page doesn't control the image bytes. E3-T3's acceptance ("revoked links never serve content, including
  from browser cache, tested with back-button") is the right test, but the mechanism is incomplete: the browser fetches the actual bytes
  directly from Supabase Storage via the signed URL, and Supabase's response headers govern that cache entry, not your page's. Short TTL mostly    saves you, but back-button within the TTL after a revoke can replay bytes from memory cache. Since you already wrote the honest test, just be
  aware the fix may need to live on the storage side (or accept and document the residual within-TTL window — defensible, since revoca  pre-verif  4. Nobody owns seeding the auth accounts. The deliverables require committed demo patient/provider/admin accounts, but E2-T1 seeds
  patient_records (clinic records, not logins) and E7-T1 seeds providers/slots (rows, not logins). Creating Supabase Auth users programmatically
  needs the admin API and is its own small chunk of work — including deciding which demo patient account is pre-verified (so images show
  instantly) versus left unlinked (so the video can demo the verification flow, which the mandated demo order requires first). Add it to a seed
  ticket explicitly.
  5. CI's test database strategy conflicts with your schema. user_profiles mirrors auth.users, which presumably means an FK into Supabase's auth
  schema. E6-T1 requires a "real DB" concurrency test in CI, but E0-T4 doesn't say what that DB is. Plain Postgres in a CI service container       won't have auth.users; supabase start in CI works but is a heavier setup. Decide now (I'd lean supabase CLI in CI, or drop the FK and treat
  user_profiles.user_id as a soft reference) — this is the kind of thing that ambushes you on day 2 of 3.
                                          6. Cache-Control: no-store on the share page doesn't control the image bytes. E3-T3's acceptance ("revoked links never serve content, including
  from browser cache, tested with back-button") is the right test, but the mechanism is incomplete: the browser fetches the actual bytes           directly from Supabase Storage via the signed URL, and Supabase's response headers govern that cache entry, not your page's. Short TTL mostly
  saves you, but back-button within the TTL after a revoke can replay bytes from memory cache. Since you already wrote the honest test, just be
  aware the fix may need to live on the storage side (or accept and document the residual within-TTL window — defensible, since revocation         "effective on next hit" is what AD6 promises anyway).
  Smaller notes

                                                                                                                                    - How is "completed visit" modeled? Core #3 gates images on completed visits, but the §6 data model shows studies → images with no visit/status
  field and no link to appointments. The seed talks about "completed visits" but the schema never defines one. One status or visit_date+status     field on studies closes it — just make it explicit before E0-T3.
  - The 24 h reminder can't be demoed. E8-T7 must show a reminder sent/received, but PD10 hardcodes 24 h before start. You need a dev-mode
  interval override or a seed appointment engineered to land in the next tick's window. One line in E5-T8 or E8-T7 fixes it.                       - E1-T4's "statistically indistinguishable response timing" acceptance is a flaky-test trap. Asserting timing distributions in CI is how you
  get red builds at 3am on a noisy runner. Keep the constant-time implementation (dummy compare — good), assert the identical error body, and
  verify timing behavior by code review or a non-CI script.
  - Record-axis lockout is a self-inflicted DoS vector — an attacker who knows a victim's patient_ref can lock the victim out by spamming wrong    DOBs. AD5's multi-axis design is still the right call for a take-home (the brief demands lockout), but acknowledge the tradeoff in the README;
  it shows you saw it.
  - Patient deletion is admin-run only. The brief says "patients can request deletion of their data"; E8-T3 gives a documented admin procedure
  with no patient-facing request path. Probably fine for grading, but a mailto/support-note in the UI would close the letter of the requirement
  cheaply.
  - tickets/*.md duplicate TICKETS.md verbatim — two sources of truth that will drift the first time you edit one. Generate one from the other,    or pick one.

                                                                                                                                     One thing I'd explicitly not change: PD1 (booking open without identity verification), PD5 (no provider image viewing), and skipping the
  non-scoring stretch items are all correct scope calls — the rubric math backs every one of them.
