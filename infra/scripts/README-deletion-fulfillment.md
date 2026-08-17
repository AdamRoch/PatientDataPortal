# Admin deletion fulfillment procedure

This procedure is intentionally outside the web UI. It is a privileged, destructive operation. Use an approved administrator workstation, the application database credential, and the Supabase service key. Do not run the execute form as part of development, CI, or against a live service without the organization’s approved deletion authorization.

1. Open `/admin/deletion-requests`, verify the request ID and the requester through the approved support process.
2. Run `infra/scripts/fulfill-deletion-request.sh REQUEST_ID` for a dry run. It only lists the affected storage paths.
3. Review the paths and request identity. Obtain the required authorization.
4. Set `DATABASE_URL`, `SUPABASE_URL`, and `SUPABASE_SERVICE_KEY`, then run `DELETION_FULFILLMENT_APPROVED=YES infra/scripts/fulfill-deletion-request.sh REQUEST_ID --execute`.
5. Preserve the command output with the case record. Verify the request no longer appears in the pending admin view and that its public share links are unavailable.

The script first purges objects in `study-assets` and `reports`. Only after each deletion succeeds does it run one database transaction that revokes shares, removes share-email payloads, removes imaging/report metadata, anonymizes patient and appointment data, and deletes `audit_subject_links`. It retains `audit_log` without a lookup to the patient. If storage deletion fails, it stops before the database transaction; rerun after resolving the failure.

For deterministic non-production verification, seed a disposable database with the repository seed command, create a request for one synthetic patient, and use a storage test double or dedicated test Supabase project. The test must assert that object paths are gone, `share_links.revoked_at` is set, share outbox payloads are absent, the patient has no identifying fields or claim, `audit_subject_links` has no matching row, and the preexisting `audit_log` row remains.
