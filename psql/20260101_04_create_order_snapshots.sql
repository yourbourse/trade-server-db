-- Order snapshots taken when an end of day statement is generated.
--
-- One row per working order of the account, holding the state of that order at the scheduled end of day time.
-- Only working orders are snapshotted - a filled, cancelled or expired order is not part of a statement.
--
-- time_statement is the same value as on the statement row in ##daily_statements_table##, which is what ties
-- the two together. The unique index on (sender_id, time_statement, order_id) deduplicates the at-least-once
-- delivery.

CREATE TABLE IF NOT EXISTS ##order_snapshots_table##
(
    id                              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                       BIGINT           NOT NULL,
    time_statement                  TIMESTAMP WITH TIME ZONE NOT NULL,
    order_id                        BIGINT           NOT NULL,
    order_history_record_id         BIGINT           NOT NULL,
    trading_account_id              BIGINT           NOT NULL,
    group_id                        BIGINT           NOT NULL,
    group_name                      VARCHAR(128)     NOT NULL,
    symbol_name                     VARCHAR(32)      NOT NULL,
    time_created                    TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified                   TIMESTAMP WITH TIME ZONE NOT NULL,
    classification_id               BIGINT           NOT NULL,
    symbol_id                       BIGINT           NOT NULL,
    parent_order_id                 BIGINT           NOT NULL,
    oco_order_id                    BIGINT           NOT NULL,
    stop_loss_order_id              BIGINT           NOT NULL,
    take_profit_order_id            BIGINT           NOT NULL,
    parent_position_id              BIGINT           NOT NULL,
    position_id                     BIGINT           NOT NULL,
    position_by_id                  BIGINT           NOT NULL,
    liquidity_connection_id         BIGINT           NOT NULL,
    volume                          NUMERIC          NOT NULL,
    remaining_volume                NUMERIC          NOT NULL,
    -- nullable: an unfilled order and an order without a stop or a limit carry NaN, which the exporter writes
    -- as NULL rather than as a NaN encoded NUMERIC
    avg_fill_price                  NUMERIC,
    stop_price                      NUMERIC,
    limit_price                     NUMERIC,
    margin_conversion_rate          NUMERIC          NOT NULL,
    lot_size                        NUMERIC          NOT NULL,
    order_type                      SMALLINT         NOT NULL,
    order_expiry                    SMALLINT         NOT NULL,
    order_side                      SMALLINT         NOT NULL,
    order_status                    SMALLINT         NOT NULL,
    group_markup                    NUMERIC          NOT NULL,
    price_decimal_places            SMALLINT         NOT NULL,
    account_currency_decimal_places SMALLINT         NOT NULL,
    -- nullable: only a Good Till Date order has an expiration time
    expiration_time                 TIMESTAMP WITH TIME ZONE,
    reason                          SMALLINT         NOT NULL,
    comment                         VARCHAR(64)      NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_sender_stmt_order_##order_snapshots_table##
    ON ##order_snapshots_table## (sender_id, time_statement, order_id);
