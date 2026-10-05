-- Current state of every live trading account, one row per (sender_id, trading_account_id).
--
-- Not written by the trade server: maintained from ##trading_account_events_table## by the trigger in
-- views/trading_accounts_projection.sql, in the same transaction as the event.
-- No foreign key on source_event_row_id: events age out of retention long before the account does.

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
    -- the event row this state came from
    source_event_row_id        BIGINT                   NOT NULL,
    -- when the projection last wrote the row
    time_applied               TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),

    PRIMARY KEY (sender_id, trading_account_id)
);
