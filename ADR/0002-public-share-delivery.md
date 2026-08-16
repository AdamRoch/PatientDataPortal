# ADR 0002: Proxy public shared content through the API

**Status:** Accepted

## Context

Supabase signed URLs remain usable until they expire. Revoking the application's share token cannot recall a signed URL already handed to a visitor. The brief requires revoked links to stop serving content.

## Decision

Authenticated patients may receive short-lived signed URLs for their own images, cine frames, and reports after authorization. Public share pages never receive a Storage URL. The API validates the share token and streams the single image or signed report on every content request.

Public share responses use `Cache-Control: private, no-store` and `Referrer-Policy: no-referrer`. The public page has no third-party scripts. Tokens are redacted from application logs.

## Consequences

- Revocation takes effect on the next public content request.
- Public deliveries can be audited as actual deliveries.
- Shared bytes consume API bandwidth, which is acceptable for rare single-file shares.
- Cine sharing remains outside v1.

## Rejected alternatives

- Mint a short-lived Storage URL after validating the share token. Revocation would only be bounded by the Storage URL and cache lifetime, not immediate.
- Delete or rotate the underlying object when one share is revoked. That would disrupt the patient's own access and other valid shares.
