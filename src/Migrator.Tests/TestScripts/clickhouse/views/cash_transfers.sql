----break----

-- Cash movements on an account: everything the trade server writes into ##trades_table## that is not a trade.
-- The shipped PostgreSQL counterpart is psql/views/cash_transfers.sql and the two carry the same columns in the
-- same order.
--
-- THIS SCRIPT IS NOT PART OF THE SHIPPED MIGRATION, for the same reason as the table definitions beside it. A
-- ClickHouse CREATE VIEW resolves the table it reads, and ##trades_table## is not created by this repository -
-- shipping the view would turn a missing table from a failing export into a failing migration. So the view
-- belongs to the deployment that owns the table; this copy is what the integration tests migrate against, and
-- it is the definition that deployment should carry. clickhouse/users/ro.sql still grants on it by name,
-- exactly as it does on the tables.
--
-- The transfer pipeline populates a fixed subset of the trades columns, and this view is that subset - the
-- fifteen columns every transfer carries, then the seven a collateral transfer adds. The rest of the trades
-- columns are left at 0 by the writer and would only invite the reader to draw conclusions from them, so they
-- are not exposed here. Read ##trades_history_view## for a trade.
--
-- The filter is the complement of the one in ##trades_history_view## rather than a list of the transfer types,
-- so the two views partition the table: a trade type the trade server gains later shows up here instead of
-- disappearing from both. Today the complement is Balance, Credit, Fee, Adjustment, Bonus, Commission, Daily
-- and Monthly Commission, Agent Commission and its daily and monthly forms, Interest, Dividend, Franked
-- Dividend, Tax, Stopout Compensation, Stopout Credit Adjustment and Credit Bonus - trade types 2 to 13 and 16
-- to 21.
--
-- A collateral transfer is one whose currency differs from the account currency; the trade server resolves a
-- symbol for it and fills the seven extra columns from that symbol and the top of its book. A plain transfer
-- leaves them at 0, which is what is_collateral distinguishes - symbol_id is the one of the seven that cannot
-- legitimately be 0 on a collateral transfer.
--
-- FINAL is what makes the view exact. The trades table is a ReplacingMergeTree and an at-least-once redelivery
-- stays visible as a duplicate row until the parts merge, which is not something a reporting view should
-- expose. It costs more than a plain read; a deployment that would rather have the speed and accept the
-- duplication window drops the keyword.
--
-- Names are qualified with the database because ON CLUSTER carries the statement to nodes whose default
-- database is not this one.

CREATE OR REPLACE VIEW ##database_name##.##cash_transfers_view## ##cluster_name_suffix##
AS
SELECT
       -- always populated
       sender_id,
       group_name,
       symbol_name,                         -- the currency the transfer was made in
       trade_id,
       time_created,
       time_modified,
       trading_account_id,
       group_id,
       trade_type,                          -- which kind of transfer this is
       notional_value_in_original_currency, -- the amount, in the transfer currency
       notional_value_in_account_currency,  -- the amount, converted to the account currency
       entry_type,                          -- In or Out; for collateral, by the sign of the amount
       reason,                              -- always Admin
       account_currency_decimal_places,
       comment,

       -- collateral transfers only: 0 on a plain transfer
       volume,
       contract_size,
       market_bid,
       market_ask,
       symbol_id,
       side,                                -- Buy or Sell, by the sign of the amount
       price_decimal_places,

       -- added by the view, not by the export
       time_inserted,
       symbol_id != 0 AS is_collateral
FROM ##database_name##.##trades_table## FINAL
WHERE trade_type NOT IN (
    0,  -- kBuy
    1,  -- kSell
    14, -- kBuyCancelled
    15  -- kSellCancelled
);
