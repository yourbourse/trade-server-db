----break----

-- Retention of the positions change stream: a Delete row is removed once its batch_time is older than
-- ##positions_delete_ttl_days## days. Every other row stays: an Upsert is replaced by the next change of its
-- position, and the snapshot markers are what the positions_state watermark is computed from.
--
-- The TTL is safe only together with min_age_to_force_merge_seconds on the table. Until a merge collapses a Delete
-- row with the earlier versions of its position, those versions sit in other parts and FINAL hides them only
-- because the Delete row has the higher sequence; removing the Delete row first would bring the position back.
-- The forced merges collapse it within minutes, with or without new inserts, and the retention is a margin far
-- above that. It is counted from batch_time, so a row that arrives late eats into it, and a Delete row that
-- arrives already past the retention is dropped before any merge.
--
-- The TTL lives here rather than in the CREATE TABLE so that a deployment sets it with
-- --variables positions_delete_ttl_days=N: the value changes the processed content, so the MD5 in the journal
-- changes and the script re-runs. With the default, it runs once.

ALTER TABLE ##database_name##.##positions_table## ##cluster_name_suffix##
MODIFY TTL toDateTime(batch_time) + INTERVAL ##positions_delete_ttl_days## DAY DELETE WHERE event_type = 'Delete';
