-- Export cursor: how many records the trade server has already committed for a given table.
--
-- The cursor row and the batch of trades are written in the same transaction, so the two can never drift apart.
-- This table is the authoritative source of truth: the local state.bin file is only a shutdown-time cache.

CREATE TABLE IF NOT EXISTS ##cursor_table##
(
    sender_id             BIGINT NOT NULL,
    table_name            TEXT   NOT NULL,
    last_written_position BIGINT NOT NULL,
    PRIMARY KEY (sender_id, table_name)
);
