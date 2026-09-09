-- Trading account lifecycle events exported by the trade server.
--
-- One row per change to a trading account. A creation or a modification carries the account's full state, never
-- a partial delta; a deletion carries none of it; and the two resync markers - see event_type - are not about an
-- account at all. Reducing this log to "which accounts are live, and what do they look like" is the job of
-- ##trading_accounts_table## and the trigger that maintains it, not of the consumer reading this one.
--
-- Order events by id, never by time_inserted. The trade server writes a batch in one transaction, together with
-- its ##cursor_table## row, and now() is the transaction's start time - so every event of a batch shares one
-- time_inserted and it cannot separate two events of the same account. id is assigned per row, in the order the
-- trade server recorded them, which is the order an account's history has to be replayed in.
--
-- Every column is NOT NULL and the exporter writes every one of them on every row, so a column that does not
-- apply carries a type-appropriate zero: introducer = 0 means "no introducing broker", comment = '' means "no
-- comment", and time_password_last_changed at the epoch means "never changed". A delete row and both marker
-- rows carry those zeros throughout, version and trading_account_id included - which is why a delete row cannot
-- be ordered into an account's history by its version. Use id.
--
-- There is no deduplication key. Delivery is at-least-once, so a redelivered event lands as a second row rather
-- than being rejected the way a duplicate trade is in ##trades_table##. The derived table tolerates that: an
-- upsert re-applied is the same upsert, a delete re-applied is a no-op, and a resent batch always carries its
-- events in their original order because the exporter resumes from the committed cursor position. What is
-- affected is a consumer reading this log directly - it can see the same logical change twice.
--
-- The account password is deliberately absent and has to stay absent. It is never present in an API response,
-- nothing here has a use for it, and leaving it out of the schema is what keeps it out of the database: the ro
-- reporting role can read every table in here.
--
-- IF NOT EXISTS is used throughout so the migrator can be pointed at a database that was provisioned by hand
-- from the Export Setup guide.

CREATE TABLE IF NOT EXISTS ##trading_account_events_table##
(
    id                         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted              TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                  BIGINT                   NOT NULL,
    -- 0 account state, 1 deletion, 2 resync started, 3 resync finished. A value this schema does not know is
    -- stored and left unprojected rather than rejected, so an event type the trade server gains later cannot
    -- stall the export of a deployment that has not migrated yet.
    event_type                 SMALLINT                 NOT NULL,
    -- 0 on both marker rows
    trading_account_id         BIGINT                   NOT NULL,
    -- the account's version as of this event; 0 on a deletion and on both marker rows
    version                    BIGINT                   NOT NULL,
    group_id                   BIGINT                   NOT NULL,
    client_id                  BIGINT                   NOT NULL,
    introducer                 BIGINT                   NOT NULL,
    time_created               TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified              TIMESTAMP WITH TIME ZONE NOT NULL,
    time_password_last_changed TIMESTAMP WITH TIME ZONE NOT NULL,

    leverage                   INTEGER                  NOT NULL,

    enabled                    BOOLEAN                  NOT NULL,
    allow_trading              BOOLEAN                  NOT NULL,
    external_account_id        VARCHAR(64)              NOT NULL DEFAULT '',
    comment                    VARCHAR(256)             NOT NULL DEFAULT ''
);

-- what the index covers comes before the table name: PostgreSQL truncates an identifier to 63 bytes, so a
-- configured table name long enough to reach the limit would otherwise leave both indexes with the same
-- truncated name, and the second CREATE ... IF NOT EXISTS would quietly do nothing.
--
-- An account's own history, and the access path the projection reads to find the newest retained event per
-- account. The primary key leads with id, so it cannot serve a filter on the account.
CREATE INDEX IF NOT EXISTS idx_account_row_##trading_account_events_table##
    ON ##trading_account_events_table## (sender_id, trading_account_id, id);

-- The resync markers, which is where the projection reads a sender's resync watermark from - the log is the
-- only record of it, there is no separate state table. Partial, because a marker pair is two rows per resync
-- against however many the resync itself carries, so this index stays a few rows per sender per resync while
-- the table grows without it. Without the predicate the same lookup would scan the sender's whole index range
-- to find two rows.
CREATE INDEX IF NOT EXISTS idx_markers_##trading_account_events_table##
    ON ##trading_account_events_table## (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
