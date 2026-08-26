----break----

-- Position snapshots taken when an end of day statement is generated, following the column list and the types of
-- the Export Setup guide.
--
-- THIS SCRIPT IS NOT PART OF THE SHIPPED MIGRATION - see the note in 20260101_01_create_trades.sql.
--
-- The nullable stop loss and take profit of the guide stay nullable here: a position without protection carries
-- NaN, and the exporter writes NULL rather than a NaN that Decimal cannot hold.

CREATE TABLE IF NOT EXISTS ##position_snapshots_table## ##cluster_name_suffix##
(
    time_inserted                   DateTime64(6, 'UTC') DEFAULT now64(6),
    sender_id                       Int64,
    time_statement                  DateTime64(6, 'UTC'),
    position_id                     Int64,
    trading_account_id              Int64,
    group_id                        Int64,
    group_name                      LowCardinality(String),
    symbol_name                     LowCardinality(String),
    symbol_id                       Int64,
    side                            Int16,
    volume                          Decimal(38, 18),
    avg_open_price                  Decimal(38, 18),
    market_price                    Decimal(38, 18),
    profit                          Decimal(38, 18),
    swaps                           Decimal(38, 18),
    commission                      Decimal(38, 18),
    fee                             Decimal(38, 18),
    margin                          Decimal(38, 18),
    stop_loss                       Nullable(Decimal(38, 18)),
    stop_loss_order_id              Int64,
    take_profit                     Nullable(Decimal(38, 18)),
    take_profit_order_id            Int64,
    stopout_order_id                Int64,
    profit_conversion_rate          Decimal(38, 18),
    margin_conversion_rate          Decimal(38, 18),
    lot_size                        Decimal(38, 18),
    time_created                    DateTime64(6, 'UTC'),
    time_modified                   DateTime64(6, 'UTC'),
    price_decimal_places            Int16,
    account_currency_decimal_places Int16,
    intraday_swap_day               Decimal(38, 18),
    intraday_swap_month             Decimal(38, 18),
    created_by_order_id             Int64,
    updated_by_order_id             Int64
)
ENGINE = ##replacing_merge_tree_engine##
PARTITION BY toYYYYMM(time_statement)
PRIMARY KEY (sender_id)
ORDER BY (sender_id, time_statement, position_id)
SETTINGS index_granularity = 8192;
