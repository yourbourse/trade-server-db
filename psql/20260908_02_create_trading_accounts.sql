-- The current state of every live trading account.
--
-- Not written by the trade server. It is derived from ##trading_account_events_table## by the trigger in
-- psql/views/trading_accounts_projection.sql, inside the same transaction as the event that caused the change -
-- so it can never lag behind the log, and a batch that fails to commit leaves no trace in it.
--
-- One row per (sender_id, trading_account_id), answering "which accounts are live, and what do they look like"
-- in a single read. It carries the same columns as a state carrying event, with the same meanings: a column
-- that does not apply carries a type-appropriate zero rather than NULL, so introducer = 0 means "no introducing
-- broker" and time_password_last_changed at the epoch means "never changed". The projection copies those values
-- through; it does not reinterpret them.
--
-- The one thing it adds is source_event_row_id, the id of the log row that produced the state. It makes a row
-- traceable back to its event, and it is what a resync's end marker compares against when it removes the
-- accounts the resync did not re-announce. There is deliberately no foreign key to the log: an event ages out
-- of the retention window long before the account does, and a foreign key would make that purge either fail or
-- cascade into here, which is the one thing this table exists not to do.
--
-- There is deliberately no companion table holding resync state either. A resync arrives as a start marker, an
-- unbounded stream of state carrying events and an end marker, and the log already records all three - so the
-- watermark a sweep needs is a lookup against the markers, not a row somebody has to keep in step. See
-- ##trading_accounts_resync_view## for the same answer in readable form.
--
-- IF NOT EXISTS throughout, for the same reason as the create scripts it follows.

CREATE TABLE IF NOT EXISTS ##trading_accounts_table##
(
    sender_id                  BIGINT                   NOT NULL,
    trading_account_id         BIGINT                   NOT NULL,
    version                    BIGINT                   NOT NULL,
    group_id                   BIGINT                   NOT NULL,
    client_id                  BIGINT                   NOT NULL,
    leverage                   INTEGER                  NOT NULL,
    introducer                 BIGINT                   NOT NULL,
    external_account_id        VARCHAR(64)              NOT NULL,
    comment                    VARCHAR(256)             NOT NULL,
    enabled                    BOOLEAN                  NOT NULL,
    allow_trading              BOOLEAN                  NOT NULL,
    time_created               TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified              TIMESTAMP WITH TIME ZONE NOT NULL,
    time_password_last_changed TIMESTAMP WITH TIME ZONE NOT NULL,
    -- the id of the log row this state came from: traceability, and the resync sweep's comparison
    source_event_row_id        BIGINT                   NOT NULL,
    -- when the projection last wrote this row, as against time_modified, when the account last changed
    time_applied               TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),

    PRIMARY KEY (sender_id, trading_account_id)
);

-- No further indexes, deliberately. The table is bounded at roughly the live account count, so a filter the
-- primary key cannot serve - by client_id or by group_id - is a scan of a small table rather than of a growing
-- one, and a resync sweep reads the sender's rows through the primary key's leading column. Per-client and
-- per-group access paths are the plausible next candidates, left until a real workload asks for them rather
-- than guessed at here.
