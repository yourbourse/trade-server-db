----break----

-- The current state of the exported positions, computed from the positions change stream on every read: one row
-- per open position of every sender. Always up to date, at the cost of a FINAL read of the whole table per query.
-- positions_state holds the same result, refreshed on a schedule, for readers that can wait for the next refresh.
--
-- This is the one definition of the state: positions_state selects from this view, so a change here reaches both
-- on the next refresh. CREATE OR REPLACE, so a changed query is applied by the next run of this script.
--
-- The watermark of a sender is the latest SnapshotStart that has a SnapshotEnd after it. A position whose last
-- change is older than the watermark was not part of the latest complete snapshot, so the server no longer knows
-- it and the view hides it. A sender appears in the view only once its first snapshot has completed; a snapshot
-- still in progress changes nothing until its SnapshotEnd arrives.
--
-- The view runs with the rights of whoever reads it (SQL SECURITY INVOKER, the default for a plain view), so a
-- reader needs SELECT on the positions table as well as on the view.
--
-- The names are qualified with the database because ON CLUSTER carries the statement to nodes whose default
-- database is not this one.

CREATE OR REPLACE VIEW ##database_name##.##positions_state_live_view## ##cluster_name_suffix##
AS
WITH
    snapshot_ends AS
    (
        SELECT sender_id, max(sequence) AS end_sequence
        FROM ##database_name##.##positions_table##
        WHERE position_id = 0 AND event_type = 'SnapshotEnd'
        GROUP BY sender_id
    ),
    watermarks AS
    (
        SELECT starts.sender_id, max(starts.sequence) AS watermark
        FROM ##database_name##.##positions_table## AS starts
        INNER JOIN snapshot_ends ON starts.sender_id = snapshot_ends.sender_id
        WHERE starts.position_id = 0
          AND starts.event_type = 'SnapshotStart'
          AND starts.sequence < snapshot_ends.end_sequence
        GROUP BY starts.sender_id
    )
SELECT positions.*
FROM ##database_name##.##positions_table## AS positions FINAL
INNER JOIN watermarks ON positions.sender_id = watermarks.sender_id
WHERE positions.position_id != 0
  AND positions.event_type = 'Upsert'
  AND positions.sequence >= watermarks.watermark
SETTINGS do_not_merge_across_partitions_select_final = 1;
