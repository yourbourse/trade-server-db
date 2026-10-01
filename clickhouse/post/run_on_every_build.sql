----break----

-- Runs on every migration. The content never changes, so the MD5 in the journal stays the same and the entry
-- is simply refreshed - it exists to make the applied version visible in the migration log.

SELECT 'Deployment complete. Database: ##database_name##, Cluster: ##cluster_name##, Ticks table: ##ticks_table##, Version: ##assembly_version##';
