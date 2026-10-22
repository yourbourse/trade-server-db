-- Free-text client tags, up to 511 characters in the API. ADD COLUMN can only append, so tags is last in both tables.
-- NOT NULL needs the defaults for the rows already there. The log keeps its default for exporters that omit tags;
-- views/clients_projection.sql drops the one on ##clients_table## once the trigger writes tags.

ALTER TABLE ##client_events_table##
    ADD COLUMN IF NOT EXISTS tags VARCHAR(512) NOT NULL DEFAULT '';

ALTER TABLE ##clients_table##
    ADD COLUMN IF NOT EXISTS tags VARCHAR(512) NOT NULL DEFAULT '';
