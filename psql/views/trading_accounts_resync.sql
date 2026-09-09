-- Whether each sender's picture of the live account set is settled, or still converging.
--
-- A resync arrives as a start marker, an unbounded stream of state carrying events, and an end marker, and it
-- may be spread over many transactions - so between the two markers ##trading_accounts_table## carries a
-- mixture: accounts the resync has already re-announced are current, accounts it has not reached yet still
-- carry their pre-resync state, and accounts deleted while the sender was down are still there until the end
-- marker removes them. Bounded and plausible rather than wrong, but a reader that cares can tell the two
-- situations apart here.
--
-- There is no table behind this. The markers in ##trading_account_events_table## are the record of every
-- resync, so the answer is an aggregate over them rather than state the projection has to keep in step - which
-- also means it cannot disagree with the log, and that dropping this view costs nothing but the convenience.
-- idx_markers_ is the partial index that keeps it to a few rows per sender.
--
-- Re-runnable: CREATE OR REPLACE preserves the grants the users phase applied. It can append a column at the
-- end but cannot rename, reorder or drop one; a change like that needs an incremental script that DROPs the
-- view first, and the run that carries it has to pass --users so the grants come back.

CREATE OR REPLACE VIEW ##trading_accounts_resync_view## AS
SELECT sender_id,

       -- the log row the newest start marker arrived as. This is the watermark the end marker compares
       -- source_event_row_id against, exposed so a support query can do the same arithmetic by hand.
       max(id) FILTER (WHERE event_type = 2)            AS resync_started_event_row_id,

       max(time_inserted) FILTER (WHERE event_type = 2) AS time_resync_started,

       -- when a resync last finished. While one is in progress this belongs to the previous resync, not to the
       -- one running - read it together with resync_in_progress, not on its own.
       max(time_inserted) FILTER (WHERE event_type = 3) AS time_resync_finished,

       -- a start marker with no end marker after it: the sender is mid-resync
       COALESCE(max(id) FILTER (WHERE event_type = 2), -1) >
       COALESCE(max(id) FILTER (WHERE event_type = 3), -1) AS resync_in_progress

FROM ##trading_account_events_table##
WHERE event_type IN (2, 3)
GROUP BY sender_id;
