ALTER TABLE cine_clips
    ADD COLUMN manifest jsonb NOT NULL DEFAULT '{"frames": [], "defaultFps": 12}'::jsonb;

ALTER TABLE cine_clips
    ADD CONSTRAINT cine_clips_manifest_shape_check
    CHECK (
        jsonb_typeof(manifest) = 'object'
        AND jsonb_typeof(manifest->'frames') = 'array'
        AND jsonb_typeof(manifest->'defaultFps') = 'number'
    );
