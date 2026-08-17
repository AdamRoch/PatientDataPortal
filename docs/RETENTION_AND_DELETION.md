# Retention and deletion

This portal is a demonstration system, not a records-retention system of record. Do not place real patient data in it. The periods below are operating policy for this repository and require legal and compliance review before any production use.

| Data | Retention | Purpose |
| --- | --- | --- |
| Images, thumbnails, cine frames, and reports | Until a verified deletion request is fulfilled | Patient viewing and provider care workflow |
| Appointment and study rows | Retained in anonymized form after fulfillment | Scheduling and aggregate operational history |
| Share links and queued share emails | Revoked and payloads removed during fulfillment | Secure delivery; no longer needed after deletion |
| Deletion request metadata | Request timestamp and fulfilled status | Demonstrate request handling without retaining the patient link |
| Audit events | Retained under the organization’s approved security-audit schedule | Security investigation and access accountability |

Audit history never stores a patient name or patient reference for this workflow. A random audit pseudonym is connected to the patient only through `audit_subject_links`. Fulfillment deletes that lookup, leaving the append-only audit event intact but no longer attributable to the patient. This implements [ADR 0006](../ADR/0006-audit-and-deletion-semantics.md).

## Request and fulfillment

A verified patient selects **Request deletion of my data** in their profile. Administrators see pending requests at `/admin/deletion-requests`. The UI intentionally cannot fulfill a request.

An authorized administrator follows [the fulfillment procedure](../infra/scripts/README-deletion-fulfillment.md). It requires a dry run, a reviewed request ID, and an explicit execution acknowledgement. Never test the execute path against a live service. The procedure is retry-safe for storage objects already removed by a prior interrupted attempt; do not mark a request fulfilled until every listed object deletion succeeds.
