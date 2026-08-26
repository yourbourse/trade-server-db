----break----

-- Order snapshots taken when an end of day statement is generated, following the column list and the types of
-- the Export Setup guide.
--
-- THIS SCRIPT IS NOT PART OF THE SHIPPED MIGRATION - see the note in 20260101_01_create_trades.sql.
--
-- The nullable prices of the guide stay nullable here: an unfilled order, and an order without a stop or a
-- limit, carries NaN, and the exporter writes NULL rather than a NaN that Decimal cannot hold.

CREATE TABLE IF NOT EXISTS ##order_snapshots_table## ##cluster_name_suffix##
(
    time_inserted                   DateTime64(6, 'UTC') DEFAULT now64(6),
    sender_id                       Int64,
    time_statement                  DateTime64(6, 'UTC'),
    order_id                        Int64,
    order_history_record_id         Int64,
    trading_account_id              Int64,
    group_id                        Int64,
    group_name                      LowCardinality(String),
    symbol_name                     LowCardinality(String),
    time_created                    DateTime64(6, 'UTC'),
    time_modified                   DateTime64(6, 'UTC'),
    classification_id               Int64,
    symbol_id                       Int64,
    parent_order_id                 Int64,
    oco_order_id                    Int64,
    stop_loss_order_id              Int64,
    take_profit_order_id            Int64,
    parent_position_id              Int64,
    position_id                     Int64,
    position_by_id                  Int64,
    liquidity_connection_id         Int64,
    volume                          Decimal(38, 18),
    remaining_volume                Decimal(38, 18),
    avg_fill_price                  Nullable(Decimal(38, 18)),
    stop_price                      Nullable(Decimal(38, 18)),
    limit_price                     Nullable(Decimal(38, 18)),
    margin_conversion_rate          Decimal(38, 18),
    lot_size                        Decimal(38, 18),
    order_type                      Int16,
    order_expiry                    Int16,
    order_side                      Int16,
    order_status                    Int16,
    group_markup                    Decimal(38, 18),
    price_decimal_places            Int16,
    account_currency_decimal_places Int16,
    expiration_time                 Nullable(DateTime64(6, 'UTC')),
    reason                          Int16,
    comment                         String
)
ENGINE = ##replacing_merge_tree_engine##
PARTITION BY toYYYYMM(time_statement)
PRIMARY KEY (sender_id)
ORDER BY (sender_id, time_statement, order_id)
SETTINGS index_granularity = 8192;
