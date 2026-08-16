# ADR 0005: Require verified email and recoverable patient claims

**Status:** Accepted

## Context

Patient ID and date of birth are low-entropy identifying facts. A first-claim-wins design without recovery can turn a mistaken or malicious claim into permanent account takeover. A hard lock keyed only by patient reference lets an attacker deny service to the real patient.

## Decision

Only an email-verified account may attempt a patient-record claim. Matching uses Patient ID and DOB and returns one generic response for all failures.

The system durably locks repeated failures by account. HMAC-scoped network and patient-reference counters throttle distributed guessing without hard-locking the record. Claims are unique, fully audited, and reversible only through an audited admin recovery procedure.

## Consequences

- An attacker cannot permanently disable a patient record by sending five bad guesses.
- Claim disputes have a defined recovery path.
- Verification tests need injected clocks and deterministic throttle state.
- Raw DOB, patient reference, and IP values must not appear in application logs.

## Rejected alternatives

- Hard-lock the patient record after repeated failures. This creates an easy denial-of-service endpoint.
- Allow claims before email confirmation. This lowers the cost of automated account creation and guessing.
