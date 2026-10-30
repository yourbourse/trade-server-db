----break----

-- Retention of the position_events change stream: a Delete row is removed once its batch_time is older than
-- ##position_events_delete_ttl_days## days. Every other row stays: an Upsert is replaced by the next change of its
-- position, and the snapshot markers are what the positions watermark is computed from.
--
-- The TTL is safe only together with min_age_to_force_merge_seconds on the table. Until a merge collapses a Delete
-- row with the earlier versions of its position, those versions sit in other parts and FINAL hides them only
-- because the Delete row has the higher sequence; removing the Delete row first would bring the position back.
-- The forced merges collapse it within minutes, with or without new inserts, and the retention is a margin far
-- above that. It is counted from batch_time, so a row that arrives late eats into it, and a Delete row that
-- arrives already past the retention is dropped before any merge.
--
-- The TTL lives here rather than in the CREATE TABLE so that a deployment sets it with
-- --variables position_events_delete_ttl_days=N: the value changes the processed content, so the MD5 in the journal
-- changes and the script re-runs. With the default, it runs once.
--
-- min_age_to_force_merge_seconds is set here too, before the TTL, for the same reason: a deployment sets it with
-- --variables position_events_min_age_to_force_merge_seconds=N and the script re-runs when it changes. Coming first, it
-- is in place before the TTL ever is. The migrator refuses a retention shorter than ten times the merge age.

ALTER TABLE ##database_name##.##position_events_table## ##cluster_name_suffix##
MODIFY SETTING min_age_to_force_merge_seconds = ##position_events_min_age_to_force_merge_seconds##;

----break----

ALTER TABLE ##database_name##.##position_events_table## ##cluster_name_suffix##
MODIFY TTL toDateTime(batch_time) + INTERVAL ##position_events_delete_ttl_days## DAY DELETE WHERE event_type = 'Delete';
