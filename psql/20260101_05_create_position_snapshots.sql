-- Position snapshots taken when an end of day statement is generated.
--
-- One row per open position of the account, holding its volume, pricing, floating profit, margin and fees at the
-- scheduled end of day time.
--
-- time_statement is the same value as on the statement row in ##daily_statements_table## and on the order
-- snapshots in ##order_snapshots_table##. The unique index on (sender_id, time_statement, position_id)
-- deduplicates the at-least-once delivery.

CREATE TABLE IF NOT EXISTS ##position_snapshots_table##
(
    id                              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                       BIGINT           NOT NULL,
    time_statement                  TIMESTAMP WITH TIME ZONE NOT NULL,
    position_id                     BIGINT           NOT NULL,
    trading_account_id              BIGINT           NOT NULL,
    group_id                        BIGINT           NOT NULL,
    group_name                      VARCHAR(128)     NOT NULL,
    symbol_name                     VARCHAR(32)      NOT NULL,
    symbol_id                       BIGINT           NOT NULL,
    side                            SMALLINT         NOT NULL,
    volume                          NUMERIC          NOT NULL,
    avg_open_price                  NUMERIC          NOT NULL,
    market_price                    NUMERIC          NOT NULL,
    profit                          NUMERIC          NOT NULL,
    swaps                           NUMERIC          NOT NULL,
    commission                      NUMERIC          NOT NULL,
    fee                             NUMERIC          NOT NULL,
    margin                          NUMERIC          NOT NULL,
    -- nullable: a position without protection carries NaN, which the exporter writes as NULL rather than as a
    -- NaN encoded NUMERIC
    stop_loss                       NUMERIC,
    stop_loss_order_id              BIGINT           NOT NULL,
    take_profit                     NUMERIC,
    take_profit_order_id            BIGINT           NOT NULL,
    stopout_order_id                BIGINT           NOT NULL,
    profit_conversion_rate          NUMERIC          NOT NULL,
    margin_conversion_rate          NUMERIC          NOT NULL,
    lot_size                        NUMERIC          NOT NULL,
    time_created                    TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified                   TIMESTAMP WITH TIME ZONE NOT NULL,
    price_decimal_places            SMALLINT         NOT NULL,
    account_currency_decimal_places SMALLINT         NOT NULL,
    intraday_swap_day               NUMERIC          NOT NULL,
    intraday_swap_month             NUMERIC          NOT NULL,
    created_by_order_id             BIGINT           NOT NULL,
    updated_by_order_id             BIGINT           NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_sender_stmt_position_##position_snapshots_table##
    ON ##position_snapshots_table## (sender_id, time_statement, position_id);
