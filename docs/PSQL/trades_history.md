<div align="center">

# trades_history

**The executed and cancelled trades, without the cash movements — a view over `trades`**

PostgreSQL · view · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Reading it](#reading-it) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Kind | View over [`trades`](trades.md) |
| Default name | `trades_history` — renamed with `--variables trades_history_view=…` |
| Rows | `trade_type` in `0` Buy, `1` Sell, `14` BuyCancelled, `15` SellCancelled — the last two reserved and not currently generated |
| Columns | Every column of `trades` except the identity `id` |
| Grants | `ro`: `SELECT` · `exporter`: none |

`trades` carries two kinds of record: a buy or a sell is a real trade, and everything else — balance, credit, fee,
commission, interest and the rest — is a cash movement on the account. This view is the trading half and
[`cash_transfers`](cash_transfers.md) is the other. The two filters are complements, so **the views partition the
table**: every row is visible through exactly one of them, and a trade type the trade server gains later surfaces
in `cash_transfers` rather than in neither.

The view name is templated even though the trade server never reads or writes it, so that a database already
owning an object called `trades_history` can move ours aside.

---

## Final SQL

The view as the migrator creates it, under the default names.

```sql
CREATE VIEW trades_history AS
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
FROM trades
WHERE trade_type IN (
    0,  -- kBuy
    1,  -- kSell
    14, -- kBuyCancelled
    15  -- kSellCancelled
);
```

---

## Columns

Every column of [`trades`](trades.md#columns), with the same types and meanings, in the same order, except `id` —
a PostgreSQL storage detail with no meaning to a reader.

---

## Reading it

- An account's trades over a period are served by `idx_account_time_trades` on the table: the view adds only the
  `trade_type` filter on top.
- Every column of a Buy or Sell is populated, so the `NaN` that non-trading records carry in their trading columns
  does not reach this view — `sum(profit)` here is safe where on the table it is not. See
  [NaN, not NULL](trades.md#nan-not-null).
- The column meanings, `side` included, are on [`trades`](trades.md#columns), and the `trade_type`, `entry_type` and
  `reason` values under its [Values](trades.md#values).
- A view carries its own privileges. `SELECT` on `trades` does not reach through to it, which is why `ro` is granted
  the view by name and `exporter` — which writes rows and does not read reports — is not granted it at all.

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/views/trades_history.sql`](../../psql/views/trades_history.sql) | views | The view, as `CREATE OR REPLACE VIEW` |
| [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | `SELECT` for `ro` |

`CREATE OR REPLACE VIEW` keeps the grants the users phase applied, and can append a column at the end. It cannot
rename, reorder or drop one: a change like that needs an incremental script that drops the view first, and the run
that carries it has to pass `--users` so the grants come back.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`trades`](trades.md) · [`cash_transfers`](cash_transfers.md)
