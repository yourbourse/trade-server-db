-- Trades exported by the trade server.
--
-- The export is append-only and at-least-once: after a restart some records may be sent again, and the unique
-- index on (sender_id, trade_id) is what turns that into deduplication instead of duplicates.
--
-- IF NOT EXISTS is used throughout so the migrator can be pointed at a database that was provisioned by hand
-- from the Export Setup guide.

CREATE TABLE IF NOT EXISTS ##trades_table##
(
    id                                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                       TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                           BIGINT           NOT NULL,
    group_name                          VARCHAR(128)     NOT NULL,
    symbol_name                         VARCHAR(32)      NOT NULL,
    trade_id                            BIGINT           NOT NULL,
    time_created                        TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified                       TIMESTAMP WITH TIME ZONE NOT NULL,
    trading_account_id                  BIGINT           NOT NULL,
    group_id                            BIGINT           NOT NULL,
    trade_type                          SMALLINT         NOT NULL,
    executed_price                      NUMERIC          NOT NULL,
    volume                              NUMERIC          NOT NULL,
    order_id                            BIGINT           NOT NULL,
    position_id                         BIGINT           NOT NULL,
    profit                              NUMERIC          NOT NULL,
    commission                          NUMERIC          NOT NULL,
    swaps                               NUMERIC          NOT NULL,
    fee                                 NUMERIC          NOT NULL,
    position_price                      NUMERIC          NOT NULL,
    profit_conversion_rate              NUMERIC          NOT NULL,
    contract_size                       NUMERIC          NOT NULL,
    notional_value_in_original_currency NUMERIC          NOT NULL,
    notional_value_in_account_currency  NUMERIC          NOT NULL,
    tick_value                          NUMERIC          NOT NULL,
    tick_size                           NUMERIC          NOT NULL,
    market_bid                          NUMERIC          NOT NULL,
    market_ask                          NUMERIC          NOT NULL,
    symbol_id                           BIGINT           NOT NULL,
    group_markup                        NUMERIC          NOT NULL,
    side                                SMALLINT         NOT NULL,
    entry_type                          SMALLINT         NOT NULL,
    reason                              SMALLINT         NOT NULL,
    price_decimal_places                SMALLINT         NOT NULL,
    account_currency_decimal_places     SMALLINT         NOT NULL,
    comment                             VARCHAR(64)      NOT NULL DEFAULT '',
    agent_commission                    NUMERIC          NOT NULL,
    notional_value_in_usd               NUMERIC          NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_##trades_table##_sender_trade
    ON ##trades_table## (sender_id, trade_id);

CREATE INDEX IF NOT EXISTS idx_##trades_table##_time_created
    ON ##trades_table## (time_created);
