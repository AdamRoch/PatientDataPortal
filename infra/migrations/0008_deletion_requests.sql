CREATE TABLE deletion_requests (
    id uuid PRIMARY KEY,
    patient_record_id uuid REFERENCES patient_records(id) ON DELETE SET NULL,
    requested_by uuid REFERENCES user_profiles(user_id) ON DELETE SET NULL,
    audit_reference uuid NOT NULL UNIQUE,
    status text NOT NULL CHECK (status IN ('pending', 'fulfilled', 'cancelled')),
    requested_at timestamptz NOT NULL DEFAULT now(),
    fulfilled_at timestamptz
);
CREATE UNIQUE INDEX deletion_requests_one_pending_per_patient_idx
    ON deletion_requests (patient_record_id) WHERE status = 'pending';
CREATE INDEX deletion_requests_pending_idx ON deletion_requests (requested_at) WHERE status = 'pending';

-- This is the only reversible association between a retained audit pseudonym and a patient.
-- The fulfillment procedure deletes it while leaving audit_log append-only.
CREATE TABLE audit_subject_links (
    audit_reference uuid PRIMARY KEY,
    patient_record_id uuid NOT NULL REFERENCES patient_records(id) ON DELETE RESTRICT UNIQUE
);

GRANT SELECT, INSERT, UPDATE, DELETE ON deletion_requests, audit_subject_links TO {{APP_DB_ROLE}};
