-- Reports must belong to the same patient as their study, and storage metadata
-- must remain an object path inside the private reports bucket rather than a URL.
ALTER TABLE studies
    ADD CONSTRAINT studies_id_patient_record_unique UNIQUE (id, patient_record_id);

ALTER TABLE reports
    ADD CONSTRAINT reports_study_patient_record_fk
        FOREIGN KEY (study_id, patient_record_id)
        REFERENCES studies (id, patient_record_id)
        ON DELETE RESTRICT,
    ADD CONSTRAINT reports_private_pdf_path_check
        CHECK (
            storage_path ~ '^reports/.+\.pdf$'
            AND storage_path !~ '^[a-z][a-z0-9+.-]*://'
        );
