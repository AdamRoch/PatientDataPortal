ALTER TABLE email_outbox DROP CONSTRAINT email_outbox_status_check;
ALTER TABLE email_outbox ADD CONSTRAINT email_outbox_status_check CHECK (status IN ('pending', 'claimed', 'sent', 'failed', 'superseded'));
