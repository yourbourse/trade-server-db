<div align="center">

# cash_transfers

**The cash movements on an account, over the columns a transfer actually populates — a view over `trades`**

PostgreSQL · view · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Collateral transfers](#collateral-transfers) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Kind | View over [`trades`](trades.md) |
| Default name | `cash_transfers` — renamed with `--variables cash_transfers_view=…` |
| Rows | Every `trade_type` except `0`, `1`, `14`, `15` — today `2`–`13` and `16`–`21` |
| Columns | The 22 the transfer pipeline populates, plus `time_inserted` and `is_collateral` |
| Grants | `ro`: `SELECT` · `exporter`: none |

Everything in `trades` that is not a trade — the non-trading records:

| `trade_type` | | `trade_type` | |
|---|---|---|---|
| `2` | Balance — deposit or withdrawal | `12` | MonthlyAgentCommission |
| `3` | Credit | `13` | Interest |
| `4` | Fee | `16` | Dividend |
| `5` | Adjustment | `17` | FrankedDividend |
| `6` | Bonus | `18` | Tax |
| `7` | Commission | `19` | StopoutCompensation |
| `8` | DailyCommission | `20` | StopoutCreditAdjustment |
| `9` | MonthlyCommission | `21` | CreditBonus |
| `10` | AgentCommission | | |
| `11` | DailyAgentCommission | | |

These are the 18 types of the Admin REST API's `Transfer` — the cash or asset transfer `/admin/transfers/query`
lists and `/admin/transfers/edit` creates. The API declares them in a different order, so the numbers are
`trade_type`'s, from the *Export Setup* guide.

The filter is the **complement** of [`trades_history`](trades_history.md)'s rather than a list of the transfer
types, so the two views partition the table: a trade type the trade server gains later shows up here instead of
disappearing from both.

The transfer pipeline populates a fixed subset of the `trades` columns, and this view is that subset. The rest hold
a default — `0`, or `NaN` for some numeric columns — and are not exposed, so nothing invites a reader to draw a
conclusion from them.

---

## Final SQL

The view as the migrator creates it, under the default names.

```sql
CREATE VIEW cash_transfers AS
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
       symbol_id <> 0 AS is_collateral
FROM trades
WHERE trade_type NOT IN (
    0,  -- kBuy
    1,  -- kSell
    14, -- kBuyCancelled
    15  -- kSellCancelled
);
```

---

## Columns

| # | Column | Populated | Description | Source in the trade server |
|---|--------|-----------|-------------|----------------------------|
| 1 | `sender_id` | always | The trade server instance that wrote the row | export pipeline configuration |
| 2 | `group_name` | always | The account's trading group | `group.GetName()` |
| 3 | `symbol_name` | always | The currency or asset the amount is in — the account currency, for example `USD`, unless it is a collateral transfer | the transfer currency |
| 4 | `trade_id` | always | The record's identifier, unique per `sender_id` | next trade id |
| 5 | `time_created` | always | When the operation was made | request timestamp |
| 6 | `time_modified` | always | Equals `time_created` | request timestamp |
| 7 | `trading_account_id` | always | The trading account | request |
| 8 | `group_id` | always | The trading group | `group.GetId()` |
| 9 | `trade_type` | always | Which kind of operation — see the table above | the transfer type |
| 10 | `notional_value_in_original_currency` | always | The amount, in the operation's currency | amount, in the transfer currency |
| 11 | `notional_value_in_account_currency` | always | The amount, in the account currency | amount, in the account currency |
| 12 | `entry_type` | always | Direction — see the note below | `kIn` / `kOut`; for collateral, by the sign of the amount |
| 13 | `reason` | always | `1` Admin, or `11` AgentCommission | `kAdmin` |
| 14 | `account_currency_decimal_places` | always | Decimal places of the account currency | `group.GetCurrencyDecimalPlaces()` |
| 15 | `comment` | always | Free text, often the context: `Daily commission (2026-03-06)`, `AgentCommission for trade 12345` | request |
| 16 | `volume` | collateral only | The amount converted at the symbol's rate | amount converted at the symbol's rate |
| 17 | `contract_size` | collateral only | The symbol's contract size | `symbol.GetLotSize()` |
| 18 | `market_bid` | collateral only | Top of the symbol's book | top of book |
| 19 | `market_ask` | collateral only | Top of the symbol's book | top of book |
| 20 | `symbol_id` | collateral only | The symbol the transfer was converted through | `symbol.GetId()` |
| 21 | `side` | collateral only | `0` Buy / `1` Sell, by the sign of the amount | `kBuy` / `kSell` by the sign of the amount |
| 22 | `price_decimal_places` | collateral only | Decimal places of the symbol's price | `symbol.GetDecimalPlaces()` |
| 23 | `time_inserted` | *added by the view* | When the row landed in the database | — |
| 24 | `is_collateral` | *added by the view* | `symbol_id <> 0` | — |

Types are those of the same columns in [`trades`](trades.md#columns); `is_collateral` is a `BOOLEAN`. The *Source*
column is the trade server code each value was traced to when the view was built.

> **Note:** Two values disagree with the *Export Setup* guide. The guide gives `entry_type` as always `0` In on a
> non-trading record, where the source mapping above has `In` or `Out` by direction; and it gives `reason` as `1`
> Admin **or `11` AgentCommission**, where the transfer pipeline writes `kAdmin` — so the AgentCommission records
> are not the transfer pipeline's, and may populate columns this view does not expose. Confirm `entry_type` with the
> trade server team before relying on it; the sign of the amount always tells a deposit from a withdrawal.

---

## Collateral transfers

A **collateral transfer** is one whose currency differs from the account currency — the API's *asset* transfer,
whose amount is in another currency or an asset. The trade server resolves a symbol for it and fills columns 16–22
from that symbol and the top of its book; a plain transfer leaves them at `0`. `is_collateral` is what tells the two apart — `symbol_id` is the one of the seven that cannot legitimately be
`0` on a collateral transfer.

An account's transfer history is served by `idx_transfers_account_time_trades`, a partial index whose predicate is
written exactly as this view's `WHERE` clause, so it holds only the cash movements.

```sql
-- one account's deposits, withdrawals and other cash movements over a month
SELECT time_created, trade_type, entry_type, notional_value_in_account_currency, is_collateral, comment
FROM cash_transfers
WHERE sender_id = 1
  AND trading_account_id = 100042
  AND time_created >= '2026-09-01' AND time_created < '2026-10-01'
ORDER BY time_created;
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/views/cash_transfers.sql`](../../psql/views/cash_transfers.sql) | views | The view, as `CREATE OR REPLACE VIEW` |
| [`psql/20260828_01_add_reporting_indexes.sql`](../../psql/20260828_01_add_reporting_indexes.sql) | incremental | `idx_transfers_account_time_trades` on `trades`, matching the view's filter |
| [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | `SELECT` for `ro` |

`CREATE OR REPLACE VIEW` keeps the grants the users phase applied, and can append a column at the end. It cannot
rename, reorder or drop one: a change like that needs an incremental script that drops the view first, and the run
that carries it has to pass `--users` so the grants come back.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`trades`](trades.md) · [`trades_history`](trades_history.md)
