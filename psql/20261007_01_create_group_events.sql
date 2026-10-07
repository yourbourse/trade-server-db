-- Group config events from the trade server; ##groups_table## is derived from it.
-- Order by id (a batch shares one time_inserted). Unused fields hold zeros. Enums are ordinals.
-- Column order is the agreed schema. No dedup key: redeliveries add rows.

CREATE TABLE IF NOT EXISTS ##group_events_table##
(
    id                            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                 TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                     BIGINT                   NOT NULL,

    -- 0 state, 1 deletion, 2 resync started, 3 resync finished
    event_type                    SMALLINT                 NOT NULL,
    -- 0 on markers
    group_id                      BIGINT                   NOT NULL,
    -- 0 on deletions and markers
    version                       BIGINT                   NOT NULL,
    name                          VARCHAR(128)             NOT NULL DEFAULT '',
    company                       VARCHAR(64)              NOT NULL DEFAULT '',
    -- NULL or 0: no emails sent
    email_service_id              BIGINT                   NULL,
    currency                      VARCHAR(16)              NOT NULL DEFAULT '',
    currency_decimal_places       INTEGER                  NOT NULL DEFAULT 0,
    interest_rate                 NUMERIC                  NOT NULL DEFAULT 0,
    free_margin_mode              INTEGER                  NOT NULL DEFAULT 0,
    stopout_value_type            INTEGER                  NOT NULL DEFAULT 0,
    stopout                       NUMERIC                  NOT NULL DEFAULT 0,
    margin_call_value_type        INTEGER                  NOT NULL DEFAULT 0,
    margin_call                   NUMERIC                  NOT NULL DEFAULT 0,
    open_position_type            INTEGER                  NOT NULL DEFAULT 0,
    exchange_trading_mode         SMALLINT                 NOT NULL DEFAULT 0,
    default_leverage              INTEGER                  NOT NULL DEFAULT 0,
    demo_deposit                  NUMERIC                  NOT NULL DEFAULT 0,
    demo_inactivity_period        INTEGER                  NOT NULL DEFAULT 0,
    max_trade_history_period      INTEGER                  NOT NULL DEFAULT 0,
    max_orders                    INTEGER                  NOT NULL DEFAULT 0,
    max_positions                 INTEGER                  NOT NULL DEFAULT 0,
    max_market_data_subscriptions INTEGER                  NOT NULL DEFAULT 0,
    send_daily_reports            BOOLEAN                  NOT NULL DEFAULT false,
    send_monthly_reports          BOOLEAN                  NOT NULL DEFAULT false,
    -- list lengths only; the lists are not exported
    symbol_group_profile_count    BIGINT                   NOT NULL DEFAULT 0,
    commission_profile_count      BIGINT                   NOT NULL DEFAULT 0
);

-- prefix first, so 63-byte truncation cannot make two names collide
CREATE INDEX IF NOT EXISTS idx_group_row_##group_events_table##
    ON ##group_events_table## (sender_id, group_id, id);

-- the end marker's watermark lookup
CREATE INDEX IF NOT EXISTS idx_markers_##group_events_table##
    ON ##group_events_table## (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
