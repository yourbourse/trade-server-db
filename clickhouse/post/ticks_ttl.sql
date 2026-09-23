----break----

-- Retention of the ticks table: rows older than ##ticks_ttl_days## days are removed, a whole part at a time
-- (the table is created with ttl_only_drop_parts). The TTL lives here rather than in the CREATE TABLE so that a
-- deployment sets it with --variables ticks_ttl_days=N: the value changes the processed content, so the MD5 in the
-- journal changes and the script re-runs. With the default, it runs once.

ALTER TABLE ##database_name##.##ticks_table## ##cluster_name_suffix##
MODIFY TTL toDateTime(created_time) + INTERVAL ##ticks_ttl_days## DAY;
