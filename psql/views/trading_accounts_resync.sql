-- Per sender: whether a resync is in progress, i.e. whether ##trading_accounts_table## may still be converging.
-- Aggregated from the resync markers in ##trading_account_events_table##; there is no table behind it.

CREATE OR REPLACE VIEW ##trading_accounts_resync_view## AS
SELECT sender_id,

       -- the watermark the end marker compares source_event_row_id against
       max(id) FILTER (WHERE event_type = 2)            AS resync_started_event_row_id,

       max(time_inserted) FILTER (WHERE event_type = 2) AS time_resync_started,

       -- belongs to the previous resync while one is in progress
       max(time_inserted) FILTER (WHERE event_type = 3) AS time_resync_finished,

       -- a start marker with no end marker after it: the sender is mid-resync
       COALESCE(max(id) FILTER (WHERE event_type = 2), -1) >
       COALESCE(max(id) FILTER (WHERE event_type = 3), -1) AS resync_in_progress

FROM ##trading_account_events_table##
WHERE event_type IN (2, 3)
GROUP BY sender_id;
