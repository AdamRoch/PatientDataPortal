#!/usr/bin/env bash
set -euo pipefail

request_id="${1:-}"
mode="${2:-}"
if [[ ! "$request_id" =~ ^[0-9a-fA-F-]{36}$ ]]; then echo "usage: $0 REQUEST_UUID [--execute]" >&2; exit 2; fi
if [[ "$mode" == "--execute" && "${DELETION_FULFILLMENT_APPROVED:-}" != "YES" ]]; then echo "Refusing execute: set DELETION_FULFILLMENT_APPROVED=YES after approval." >&2; exit 2; fi
: "${DATABASE_URL:?DATABASE_URL is required}"

paths=$(psql "$DATABASE_URL" -At -v request_id="$request_id" <<'SQL'
SELECT CASE WHEN kind = 'report' THEN 'reports' ELSE 'study-assets' END || E'\t' || path
FROM (
  SELECT 'image' AS kind, images.storage_path AS path FROM deletion_requests JOIN studies ON studies.patient_record_id = deletion_requests.patient_record_id JOIN images ON images.study_id = studies.id WHERE deletion_requests.id = :'request_id' AND deletion_requests.status = 'pending'
  UNION ALL SELECT 'image', images.thumbnail_path FROM deletion_requests JOIN studies ON studies.patient_record_id = deletion_requests.patient_record_id JOIN images ON images.study_id = studies.id WHERE deletion_requests.id = :'request_id' AND deletion_requests.status = 'pending'
  UNION ALL SELECT 'cine', cine_clips.storage_path FROM deletion_requests JOIN studies ON studies.patient_record_id = deletion_requests.patient_record_id JOIN cine_clips ON cine_clips.study_id = studies.id WHERE deletion_requests.id = :'request_id' AND deletion_requests.status = 'pending'
  UNION ALL SELECT 'cine', frame->>'path' FROM deletion_requests JOIN studies ON studies.patient_record_id = deletion_requests.patient_record_id JOIN cine_clips ON cine_clips.study_id = studies.id CROSS JOIN LATERAL jsonb_array_elements(cine_clips.manifest->'frames') AS frame WHERE deletion_requests.id = :'request_id' AND deletion_requests.status = 'pending' AND frame ? 'path'
  UNION ALL SELECT 'report', reports.storage_path FROM deletion_requests JOIN reports ON reports.patient_record_id = deletion_requests.patient_record_id WHERE deletion_requests.id = :'request_id' AND deletion_requests.status = 'pending'
) paths ORDER BY 1, 2;
SQL
)
if [[ -z "$paths" ]]; then echo "No pending deletion request found: $request_id" >&2; exit 1; fi
printf '%s\n' "$paths"
[[ "$mode" == "--execute" ]] || { echo "Dry run only. Re-run with --execute after approval."; exit 0; }
: "${SUPABASE_URL:?SUPABASE_URL is required}"; : "${SUPABASE_SERVICE_KEY:?SUPABASE_SERVICE_KEY is required}"
while IFS=$'\t' read -r bucket path; do
  encoded=$(python3 -c 'import sys, urllib.parse; print("/".join(urllib.parse.quote(p, safe="") for p in sys.argv[1].split("/")))' "$path")
  curl --fail-with-body --silent --show-error -X DELETE "$SUPABASE_URL/storage/v1/object/$bucket/$encoded" -H "apikey: $SUPABASE_SERVICE_KEY" -H "Authorization: Bearer $SUPABASE_SERVICE_KEY" >/dev/null
done <<< "$paths"
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -v request_id="$request_id" <<'SQL'
BEGIN;
WITH request AS (SELECT * FROM deletion_requests WHERE id = :'request_id' AND status = 'pending' FOR UPDATE),
patient AS (SELECT patient_record_id FROM request),
shares AS (SELECT share_links.id FROM share_links LEFT JOIN images ON share_links.resource_type = 'image' AND share_links.resource_id = images.id LEFT JOIN studies ON images.study_id = studies.id LEFT JOIN reports ON share_links.resource_type = 'report' AND share_links.resource_id = reports.id WHERE (share_links.resource_type = 'image' AND studies.patient_record_id = (SELECT patient_record_id FROM request)) OR (share_links.resource_type = 'report' AND reports.patient_record_id = (SELECT patient_record_id FROM request)))
UPDATE share_links SET revoked_at = COALESCE(revoked_at, CURRENT_TIMESTAMP), recipient_email = 'deleted-' || id || '@invalid.local' WHERE id IN (SELECT id FROM shares);
DELETE FROM email_outbox WHERE kind = 'share' AND idempotency_key IN (SELECT 'share/' || id FROM shares);
DELETE FROM reports WHERE patient_record_id = (SELECT patient_record_id FROM request);
DELETE FROM images WHERE study_id IN (SELECT id FROM studies WHERE patient_record_id = (SELECT patient_record_id FROM request));
DELETE FROM cine_clips WHERE study_id IN (SELECT id FROM studies WHERE patient_record_id = (SELECT patient_record_id FROM request));
DELETE FROM studies WHERE patient_record_id = (SELECT patient_record_id FROM request);
UPDATE appointments SET patient_user_id = (SELECT id FROM request) WHERE patient_user_id = (SELECT requested_by FROM request);
UPDATE appointment_events SET actor_user_id = NULL WHERE actor_user_id = (SELECT requested_by FROM request);
UPDATE user_profiles SET display_name = 'Deleted patient', tz = 'UTC' WHERE user_id = (SELECT requested_by FROM request);
UPDATE patient_records SET patient_ref = 'DELETED-' || id, full_name = 'Deleted patient', dob = DATE '1900-01-01', claimed_by = NULL, claimed_at = NULL WHERE id = (SELECT patient_record_id FROM request);
DELETE FROM audit_subject_links WHERE audit_reference = (SELECT audit_reference FROM request);
UPDATE deletion_requests SET status = 'fulfilled', fulfilled_at = CURRENT_TIMESTAMP, patient_record_id = NULL, requested_by = NULL WHERE id = :'request_id' AND status = 'pending';
INSERT INTO audit_log (id, actor_role, action, target_type, target_reference, result) VALUES (:'request_id', 'system', 'deletion_fulfilled', 'deletion_request', :'request_id', 'allowed');
COMMIT;
SQL
