----break----

-- End of day statements exported by the trade server, following the column list and the types of the Export
-- Setup guide.
--
-- THIS SCRIPT IS NOT PART OF THE SHIPPED MIGRATION - see the note in 20260101_01_create_trades.sql. ClickHouse
-- tables are not provisioned by this repository; clickhouse/users/ only grants on them by name. The script is
-- kept here so the ClickHouse integration tests have a schema to migrate against, and it is applied by passing
-- this folder to --scripts.
--
-- Two things in the guide have no ClickHouse equivalent:
--   * the identity column: ClickHouse does not generate row ids, and the sorting key already identifies a row.
--   * the unique index on (sender_id, time_statement, trading_account_id): ClickHouse has no unique constraint,
--     so deduplication of at-least-once deliveries is left to the ReplacingMergeTree sorting key. Duplicates
--     collapse when the parts merge - query with FINAL when they must not be visible before that.
--
-- Note that Decimal cannot represent NaN, so an exporter sending NaN in a monetary field is rejected by these
-- columns and has to write 0 instead.

CREATE TABLE IF NOT EXISTS ##daily_statements_table## ##cluster_name_suffix##
(
    time_inserted                   DateTime64(6, 'UTC') DEFAULT now64(6),
    sender_id                       Int64,
    time_statement                  DateTime64(6, 'UTC'),
    group_name                      LowCardinality(String),
    trading_account_id              Int64,
    group_id                        Int64,
    statement_client_name           String,
    account_currency                LowCardinality(String),
    email                           String,
    current_balance                 Decimal(38, 18),
    prev_day_balance                Decimal(38, 18),
    current_credit                  Decimal(38, 18),
    prev_day_credit                 Decimal(38, 18),
    current_floating_profit         Decimal(38, 18),
    lifetime_realized_profit        Decimal(38, 18),
    current_equity                  Decimal(38, 18),
    prev_day_equity                 Decimal(38, 18),
    current_margin                  Decimal(38, 18),
    current_collateral              Decimal(38, 18),
    prev_day_collateral             Decimal(38, 18),
    open_positions_swaps            Decimal(38, 18),
    closed_positions_swaps          Decimal(38, 18),
    commissions                     Decimal(38, 18),
    agent_commissions               Decimal(38, 18),
    fees                            Decimal(38, 18),
    interest                        Decimal(38, 18),
    deposits                        Decimal(38, 18),
    withdrawals                     Decimal(38, 18),
    credit_transactions             Decimal(38, 18),
    adjustment_transactions         Decimal(38, 18),
    balance_bonus_transactions      Decimal(38, 18),
    credit_bonus_transactions       Decimal(38, 18),
    dividend_transactions           Decimal(38, 18),
    tax                             Decimal(38, 18),
    stopout_compensations           Decimal(38, 18),
    stopout_credit_adjustments      Decimal(38, 18),
    volume_in_usd                   Decimal(38, 18),
    volume_in_account_currency      Decimal(38, 18),
    realized_profit                 Decimal(38, 18),
    account_currency_decimal_places Int16
)
ENGINE = ##replacing_merge_tree_engine##
PARTITION BY toYYYYMM(time_statement)
-- the primary key is the sparse index ClickHouse holds in memory, so it stays at the low cardinality column;
-- the sorting key carries the rest of the guide's unique key because ReplacingMergeTree deduplicates on it, and
-- dropping trading_account_id would collapse every account of one statement run into a single row
PRIMARY KEY (sender_id)
ORDER BY (sender_id, time_statement, trading_account_id)
SETTINGS index_granularity = 8192;
