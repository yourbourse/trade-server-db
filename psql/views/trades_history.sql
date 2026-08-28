-- Executed and cancelled trades, with the accounting records left out.
--
-- The trades table carries both kinds of record: a Buy or a Sell is a real trade, everything else - Balance,
-- Credit, Fee, Commission, Interest and the rest - is a cash movement on the account. This view is the trading
-- half and ##cash_transfers_view## is the other one. The two partition the table by trade_type, so every row is
-- visible through exactly one of them and a trade type added to the trade server later surfaces in the cash
-- transfers view rather than in neither.
--
-- Every column of ##trades_table## is exposed except the identity column, which is a PostgreSQL storage detail
-- with no ClickHouse counterpart - keeping it out is what lets both engines carry the same view.
--
-- Re-runnable: CREATE OR REPLACE preserves the grants the users phase applied. It can append a column at the
-- end but cannot rename, reorder or drop one; a change like that needs an incremental script that DROPs the
-- view first, and the run that carries it has to pass --users so the grants come back.

CREATE OR REPLACE VIEW ##trades_history_view## AS
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
FROM ##trades_table##
WHERE trade_type IN (
    0,  -- kBuy
    1,  -- kSell
    14, -- kBuyCancelled
    15  -- kSellCancelled
);
