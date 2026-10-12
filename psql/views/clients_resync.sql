-- Per sender: whether ##clients_table## may still be converging, from the markers in ##client_events_table##.
-- A sender that never resynced is absent.

CREATE OR REPLACE VIEW ##clients_resync_view## AS
SELECT sender_id,

       -- the sweep's watermark
       max(id) FILTER (WHERE event_type = 2)            AS resync_started_event_row_id,

       max(time_inserted) FILTER (WHERE event_type = 2) AS time_resync_started,

       -- the previous resync's while one is in progress
       max(time_inserted) FILTER (WHERE event_type = 3) AS time_resync_finished,

       -- a start marker with no end marker after it
       COALESCE(max(id) FILTER (WHERE event_type = 2), -1) >
       COALESCE(max(id) FILTER (WHERE event_type = 3), -1) AS resync_in_progress

FROM ##client_events_table##
WHERE event_type IN (2, 3)
GROUP BY sender_id;
