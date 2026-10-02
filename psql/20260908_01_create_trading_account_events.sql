-- Trading account lifecycle events exported by the trade server; ##trading_accounts_table## is derived from it.
--
-- Order by id, not time_inserted: every event of a batch shares one time_inserted.
-- Columns that do not apply carry a zero ('' / 0 / epoch), never NULL. The password is deliberately absent.
-- No deduplication key: delivery is at-least-once, so a redelivered event lands as a second row.

CREATE TABLE IF NOT EXISTS ##trading_account_events_table##
(
    id                         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted              TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                  BIGINT                   NOT NULL,
    -- 0 account state, 1 deletion, 2 resync started, 3 resync finished; unknown values are stored, not projected
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

-- index names lead with what they cover so 63-byte truncation cannot make two collide

-- an account's history; used by the projection's reconcile
CREATE INDEX IF NOT EXISTS idx_account_row_##trading_account_events_table##
    ON ##trading_account_events_table## (sender_id, trading_account_id, id);

-- resync markers only, for the watermark lookup at the end of a resync
CREATE INDEX IF NOT EXISTS idx_markers_##trading_account_events_table##
    ON ##trading_account_events_table## (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
