----break----

-- Executed and cancelled trades, with the accounting records left out. The shipped PostgreSQL counterpart is
-- psql/views/trades_history.sql and the two carry the same columns in the same order.
--
-- THIS SCRIPT IS NOT PART OF THE SHIPPED MIGRATION, for the same reason as the table definitions beside it. A
-- ClickHouse CREATE VIEW resolves the table it reads, and ##trades_table## is not created by this repository -
-- shipping the view would turn a missing table from a failing export into a failing migration. So the view
-- belongs to the deployment that owns the table; this copy is what the integration tests migrate against, and
-- it is the definition that deployment should carry - along with the grants, which clickhouse/users/ro.sql
-- does not apply to the views the way it does to the tables.
--
-- The trades table carries both kinds of record: a Buy or a Sell is a real trade, everything else - Balance,
-- Credit, Fee, Commission, Interest and the rest - is a cash movement on the account. This view is the trading
-- half and ##cash_transfers_view## is the other one. The two partition the table by trade_type, so every row is
-- visible through exactly one of them and a trade type added to the trade server later surfaces in the cash
-- transfers view rather than in neither.
--
-- FINAL is what makes the view exact. The trades table is a ReplacingMergeTree and an at-least-once redelivery
-- stays visible as a duplicate row until the parts merge, which is not something a reporting view should
-- expose. It costs more than a plain read; a deployment that would rather have the speed and accept the
-- duplication window drops the keyword.
--
-- Names are qualified with the database because ON CLUSTER carries the statement to nodes whose default
-- database is not this one.

CREATE OR REPLACE VIEW ##database_name##.##trades_history_view## ##cluster_name_suffix##
AS
SELECT time_inserted,
       sender_id,
       group_name,
       symbol_name,
       trade_id,
       time_created,
       time_modified,
       trading_account_id,
       group_id,
       trade_type,
       executed_price,
       volume,
       order_id,
       position_id,
       profit,
       commission,
       swaps,
       fee,
       position_price,
       profit_conversion_rate,
       contract_size,
       notional_value_in_original_currency,
       notional_value_in_account_currency,
       tick_value,
       tick_size,
       market_bid,
       market_ask,
       symbol_id,
       group_markup,
       side,
       entry_type,
       reason,
       price_decimal_places,
       account_currency_decimal_places,
       comment,
       agent_commission,
       notional_value_in_usd
FROM ##database_name##.##trades_table## FINAL
WHERE trade_type IN (
    0,  -- kBuy
    1,  -- kSell
    14, -- kBuyCancelled
    15  -- kSellCancelled
);
