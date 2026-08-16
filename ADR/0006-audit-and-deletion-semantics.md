# ADR 0006: Audit observable events and pseudonymize retained history

**Status:** Accepted

## Context

The application can observe when it grants an authenticated signed URL, but it cannot observe every later Storage fetch. Public shares are different because their bytes pass through the API. The deletion flow must also coexist with an append-only audit history.

## Decision

Authenticated content authorization records `content_access_granted`. API-proxied public delivery records `shared_content_delivered`. The system does not call a signed-URL grant proof of every later byte read.

Audit rows are append-only to the application role. They retain pseudonymous actor and target references according to the documented retention policy. Patient deletion purges Storage objects, disables shares, anonymizes operational rows, and removes the lookup that connects retained audit references to the patient.

## Consequences

- Audit claims match evidence the system actually possesses.
- Deletion can remove direct identity while retaining security history.
- The retention document must name each retained field and purpose.
- Tests must prove application-role update/delete denial and deletion unlinkability.

## Rejected alternatives

- Claim every Storage fetch was audited. The application cannot prove that.
- Delete all audit history with the patient. That destroys the security record the audit log exists to preserve.
