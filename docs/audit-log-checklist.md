# Audit-log completeness checklist

This checklist records application-observable events. `content_access_granted` means the API authorized and issued a signed URL; it does **not** claim that Storage later served bytes. `shared_content_delivered` is used only where the public-share API proxied bytes itself.

| Endpoint or path | Scope | Audit event(s) | Result |
| --- | --- | --- | --- |
| `GET /api/profile` | Patient profile read | `profile_viewed` | allowed |
| `PUT /api/profile` | Profile preference update | No PHI/status audit required; authorization denial is cross-cutting | — |
| `GET /api/identity/status` | Claim-state check | No clinical PHI is returned; authorization denial is cross-cutting | — |
| `POST /api/identity/verify` | Identity claim attempt | `identity_verification`, `patient_claim` | allowed or denied |
| `POST /api/admin/patient-claims/{id}/unlink` | Claim recovery | `patient_claim_unlink`, `patient_claim_recovery` | allowed |
| `GET /api/studies` | Completed-study PHI list | `study_list_viewed` | allowed |
| `GET /api/images/{id}` | Signed image URL authorization | `content_access_granted` or `content_access_denied` | allowed or denied |
| `GET /api/reports` | Signed-report metadata list | `report_list_viewed` | allowed |
| `GET /api/reports/{id}/view` | Signed report URL authorization | `content_access_granted` or `content_access_denied` | allowed or denied |
| `GET /api/cine/{id}` and `POST /api/cine/{id}/frame-urls` | Cine manifest or signed-frame URL authorization | `content_access_granted` or `content_access_denied` | allowed or denied |
| `POST /api/share` | Share mint | `share_minted` or `share_mint_denied` | allowed or denied |
| `GET /api/shares` | Share records and recipient addresses | `share_list_viewed` | allowed |
| `DELETE /api/shares/{id}` | Share revocation | `share_revoked` or `share_revoke_denied` | allowed or denied |
| `GET /api/public/share/{token}` | Public share landing metadata | No PHI bytes are delivered; unavailable links use `shared_content_denied` | denied when unavailable |
| `GET /api/public/share/{token}/content` | API-proxied public bytes | `shared_content_delivered` or `shared_content_denied` | allowed or denied |
| `GET /api/appointments` | Patient appointment list | `appointment_list_viewed` | allowed |
| `POST /api/appointments` | Booking | `appointment_booked` | allowed |
| `POST /api/appointments/{id}/reschedule` | Booking change | `appointment_rescheduled` | allowed |
| `DELETE /api/appointments/{id}` | Booking cancellation | `appointment_cancelled` | allowed |
| `GET /api/provider/appointments` | Provider schedule with appointment data | `provider_appointment_schedule_viewed` | allowed |
| `PATCH /api/appointments/{id}/status` | Provider/admin appointment status change | `appointment_completed`, `appointment_cancelled`, or `appointment_no-show` | allowed |
| `GET /api/admin/email-outbox` | Delivery status containing recipients | `email_outbox_status_view` | allowed |

All protected endpoints also use the authorization layer for `authentication_denied`, `authorization_denied`, and, on verified-patient routes, `verified_patient_required`. These are request-level denials and intentionally do not reveal a protected resource's existence.

`GET /health`, `/WeatherForecast`, provider discovery/slot availability, provider schedule configuration, and the outbox job endpoint do not return patient clinical data, share contents, booking records, or appointment status history, so they are outside this checklist.

## Database append-only verification

`infra/migrations/0002_application_role_grants.sql` revokes `UPDATE` and `DELETE` on `audit_log` from the application role. `MigrationVerifier.VerifyAsync` executes both mutations through that role and requires PostgreSQL to return `insufficient_privilege`; the root CI migration verification runs this verifier against disposable Postgres.
