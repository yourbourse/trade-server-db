-- Per sender: whether ##groups_table## is mid-resync, from the markers in ##group_events_table##.
-- Senders that never resynced are absent.

CREATE OR REPLACE VIEW ##groups_resync_view## AS
SELECT sender_id,

       -- the sweep's watermark
       max(id) FILTER (WHERE event_type = 2)            AS resync_started_event_row_id,

       max(time_inserted) FILTER (WHERE event_type = 2) AS time_resync_started,

       -- the previous resync's while one is in progress
       max(time_inserted) FILTER (WHERE event_type = 3) AS time_resync_finished,

       -- a start marker with no end marker after it
       COALESCE(max(id) FILTER (WHERE event_type = 2), -1) >
       COALESCE(max(id) FILTER (WHERE event_type = 3), -1) AS resync_in_progress

FROM ##group_events_table##
WHERE event_type IN (2, 3)
GROUP BY sender_id;
