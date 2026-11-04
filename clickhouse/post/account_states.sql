----break----

-- The current state of the exported trading accounts, stored: a refreshable materialized view over
-- account_states_live, recomputed every ##account_states_refresh_seconds## seconds. Readers get a plain table and
-- never pay for FINAL themselves; what they read is as old as the last refresh. account_states_live computes the
-- same rows on every read.
--
-- Created here rather than in an incremental script because it selects from account_states_live, which the
-- views phase creates after the incremental scripts. IF NOT EXISTS, so a re-run keeps the view and its rows.
--
-- The target is a plain MergeTree on a cluster too: every node refreshes the view from its own replica of the
-- account_state_events table, so there is nothing to replicate and replicas must not overwrite each other's
-- refreshes.
--
-- The refresh runs as the account that ran the migration, which ClickHouse records as the view's definer.
--
-- Refreshable materialized views left experimental in ClickHouse 24.10, the oldest release this script runs on.
--
-- The names are qualified with the database because ON CLUSTER carries the statement to nodes whose default
-- database is not this one.

CREATE MATERIALIZED VIEW IF NOT EXISTS ##database_name##.##account_states_view## ##cluster_name_suffix##
REFRESH EVERY ##account_states_refresh_seconds## SECOND
ENGINE = MergeTree
ORDER BY (sender_id, trading_account_id)
AS
SELECT *
FROM ##database_name##.##account_states_live_view##;

----break----

-- keeps the refresh period of a view created by an earlier run in line with ##account_states_refresh_seconds##:
-- the value changes the processed content, so the MD5 in the journal changes and the script re-runs. MODIFY
-- REFRESH changes the schedule only: the view keeps its rows and its query.

ALTER TABLE ##database_name##.##account_states_view## ##cluster_name_suffix##
MODIFY REFRESH EVERY ##account_states_refresh_seconds## SECOND;
