-- Current state of every live group, kept by the trigger in views/groups_projection.sql.
-- No FK on source_event_row_id: events age out first. No unique name: a resync can briefly hold two.

CREATE TABLE IF NOT EXISTS ##groups_table##
(
    sender_id                     BIGINT                   NOT NULL,
    group_id                      BIGINT                   NOT NULL,
    version                       BIGINT                   NOT NULL,
    name                          VARCHAR(128)             NOT NULL,
    company                       VARCHAR(64)              NOT NULL,
    -- nullable as in the log, or a NULL event would stall the export
    email_service_id              BIGINT                   NULL,
    currency                      VARCHAR(16)              NOT NULL,
    currency_decimal_places       INTEGER                  NOT NULL,
    interest_rate                 NUMERIC                  NOT NULL,
    free_margin_mode              INTEGER                  NOT NULL,
    stopout_value_type            INTEGER                  NOT NULL,
    stopout                       NUMERIC                  NOT NULL,
    margin_call_value_type        INTEGER                  NOT NULL,
    margin_call                   NUMERIC                  NOT NULL,
    open_position_type            INTEGER                  NOT NULL,
    exchange_trading_mode         SMALLINT                 NOT NULL,
    default_leverage              INTEGER                  NOT NULL,
    demo_deposit                  NUMERIC                  NOT NULL,
    demo_inactivity_period        INTEGER                  NOT NULL,
    max_trade_history_period      INTEGER                  NOT NULL,
    max_orders                    INTEGER                  NOT NULL,
    max_positions                 INTEGER                  NOT NULL,
    max_market_data_subscriptions INTEGER                  NOT NULL,
    send_daily_reports            BOOLEAN                  NOT NULL,
    send_monthly_reports          BOOLEAN                  NOT NULL,
    symbol_group_profile_count    BIGINT                   NOT NULL,
    commission_profile_count      BIGINT                   NOT NULL,

    source_event_row_id           BIGINT                   NOT NULL,
    time_applied                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),

    PRIMARY KEY (sender_id, group_id)
);
