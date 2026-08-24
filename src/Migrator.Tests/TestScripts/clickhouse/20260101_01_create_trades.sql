----break----

-- Trades exported by the trade server, following the column list and the types of the Export Setup guide.
--
-- THIS SCRIPT IS NOT PART OF THE SHIPPED MIGRATION. ClickHouse trades and cursor tables are not provisioned by
-- this repository - the table lives wherever the deployment puts it, and clickhouse/users/ only grants on it by
-- name. The script is kept here so the ClickHouse integration tests have a schema to migrate against, and it is
-- applied by passing this folder to --scripts.
--
-- The PostgreSQL types map onto ClickHouse as: BIGINT -> Int64, SMALLINT -> Int16, NUMERIC -> Decimal(38, 18),
-- VARCHAR -> String, TIMESTAMP WITH TIME ZONE -> DateTime64(6, 'UTC').
--
-- Two things in the guide have no ClickHouse equivalent:
--   * the identity column: ClickHouse does not generate row ids, and the sorting key already identifies a row.
--   * the unique index on (sender_id, trade_id): ClickHouse has no unique constraint, so deduplication of
--     at-least-once deliveries is left to the ReplacingMergeTree sorting key. Duplicates collapse when the
--     parts merge - query with FINAL when they must not be visible before that.
--
-- Note that Decimal cannot represent NaN. The guide states that non-trading records (Balance, Credit, Fee and
-- the other accounting types) carry 0 or NaN in the fields that do not apply to them, so an exporter that
-- sends NaN will be rejected by these columns and has to write 0 instead.

CREATE TABLE IF NOT EXISTS ##trades_table## ##cluster_name_suffix##
(
    time_inserted                       DateTime64(6, 'UTC') DEFAULT now64(6),
    sender_id                           Int64,
    group_name                          LowCardinality(String),
    symbol_name                         LowCardinality(String),
    trade_id                            Int64,
    time_created                        DateTime64(6, 'UTC'),
    time_modified                       DateTime64(6, 'UTC'),
    trading_account_id                  Int64,
    group_id                            Int64,
    trade_type                          Int16,
    executed_price                      Decimal(38, 18),
    volume                              Decimal(38, 18),
    order_id                            Int64,
    position_id                         Int64,
    profit                              Decimal(38, 18),
    commission                          Decimal(38, 18),
    swaps                               Decimal(38, 18),
    fee                                 Decimal(38, 18),
    position_price                      Decimal(38, 18),
    profit_conversion_rate              Decimal(38, 18),
    contract_size                       Decimal(38, 18),
    notional_value_in_original_currency Decimal(38, 18),
    notional_value_in_account_currency  Decimal(38, 18),
    tick_value                          Decimal(38, 18),
    tick_size                           Decimal(38, 18),
    market_bid                          Decimal(38, 18),
    market_ask                          Decimal(38, 18),
    symbol_id                           Int64,
    group_markup                        Decimal(38, 18),
    side                                Int16,
    entry_type                          Int16,
    reason                              Int16,
    price_decimal_places                Int16,
    account_currency_decimal_places     Int16,
    comment                             String DEFAULT '',
    agent_commission                    Decimal(38, 18),
    notional_value_in_usd               Decimal(38, 18),
    INDEX idx_time_created time_created TYPE minmax GRANULARITY 4
)
ENGINE = ##replacing_merge_tree_engine##
PARTITION BY toYYYYMM(time_created)
-- the primary key is the sparse index ClickHouse holds in memory, so it stays at the low cardinality column;
-- the sorting key carries trade_id as well because ReplacingMergeTree deduplicates on it, and dropping it
-- would collapse two different trades that happen to share a sender and a timestamp
PRIMARY KEY (sender_id)
ORDER BY (sender_id, trade_id, time_created)
SETTINGS index_granularity = 8192;
