----break----

-- Retention of the ticks table: rows older than ##ticks_ttl_days## days are removed, and a part is dropped as a
-- whole once every row in it has expired (ttl_only_drop_parts), which the monthly partitioning makes cheap. The
-- TTL lives here rather than in the CREATE TABLE so that a deployment sets it with --variables ticks_ttl_days=N:
-- the value changes the processed content, so the MD5 in the journal changes and the script re-runs. With the
-- default, it runs once.

ALTER TABLE ##database_name##.##ticks_table## ##cluster_name_suffix##
MODIFY SETTING ttl_only_drop_parts = 1;

----break----

ALTER TABLE ##database_name##.##ticks_table## ##cluster_name_suffix##
MODIFY TTL toDateTime(created_time) + INTERVAL ##ticks_ttl_days## DAY;
