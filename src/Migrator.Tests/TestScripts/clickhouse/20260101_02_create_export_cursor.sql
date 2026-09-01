----break----

-- Export cursor, with the three columns of the Export Setup guide.
--
-- THIS SCRIPT IS NOT PART OF THE SHIPPED MIGRATION - see the note in 20260101_01_create_trades.sql. It is kept
-- so the ClickHouse integration tests have a cursor table to migrate and to grant against.
--
-- The guide's primary key on (sender_id, table_name) becomes the sorting key. ClickHouse has no UPDATE, so the
-- exporter appends a new position rather than updating in place and ReplacingMergeTree collapses the history:
-- last_written_position doubles as the version column, so the highest position always wins regardless of the
-- order the parts merge in. Read it with FINAL to get the current position.

CREATE TABLE IF NOT EXISTS ##cursor_table## ##cluster_name_suffix##
(
    sender_id             Int64,
    table_name            LowCardinality(String),
    last_written_position Int64
)
ENGINE = ##replacing_merge_tree_engine##(last_written_position)
PRIMARY KEY (sender_id, table_name)
ORDER BY (sender_id, table_name)
SETTINGS index_granularity = 8192;
