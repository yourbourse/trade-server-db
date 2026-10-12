<div align="center">

# trades

**Every exported trade and every cash movement on an account — one row per record, append-only**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Values](#values) · [NaN, not NULL](#nan-not-null) · [Indexes](#indexes) · [Reading it](#reading-it) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `trades` — renamed with `--variables trades_table=…` |
| Written by | The trade server's `trade` pipeline — `"entity": "trade"`, `"target": "postgresql"` in `export_config.json` |
| One row per | Record, written as it happens: an executed trade or a cash movement on the account |
| Deduplicated by | `UNIQUE (sender_id, trade_id)` — a guard; see [Indexes](#indexes) |
| Grants | `exporter`: `INSERT`, `SELECT` · `ro`: `SELECT` |
| Read through | [`trades_history`](trades_history.md) for the trades, [`cash_transfers`](cash_transfers.md) for the cash movements |

> **Important:** The *Export Setup* guide's example configuration names this table **`trades_export`**, while the
> migrator's default is `trades`. When the `trade` pipeline's `table` field names anything but `trades`, every
> migration has to pass that name — `--variables trades_table=trades_export` for the guide's example.

The table holds two kinds of record, told apart by [`trade_type`](#trade_type):

- **Trading records** — `0` Buy and `1` Sell, actual market executions. Every column is populated.
- **Non-trading records** — `2` Balance to `13` Interest and `16` Dividend to `21` CreditBonus: deposits,
  withdrawals, credit, fees, commissions, adjustments, interest, dividends, tax. Most trading columns do not apply
  and hold a default, `0` or `NaN`; the meaningful data is in the two `notional_value_*` amounts and in `comment`.

A reader almost always wants one kind or the other, which is what the two views over this table are for. Their
filters are complements, so every row is visible through exactly one of them.

The export cursor is written in the same transaction as each batch, so in normal operation a record is written
exactly once; the unique index on `(sender_id, trade_id)` is a guard for the one case that bypasses the cursor — see
[Indexes](#indexes).

---

## Final SQL

The table as it stands once both scripts under [Scripts](#scripts) have run, with the default names substituted.
On an empty database it creates the same table, indexes and constraints the migrator does. A deployment that
renamed the table renames every index with it — each index name ends in the table name.

```sql
CREATE TABLE trades
(
    id                                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                       TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                           BIGINT                   NOT NULL,
    group_name                          VARCHAR(128)             NOT NULL,
    symbol_name                         VARCHAR(32)              NOT NULL,
    trade_id                            BIGINT                   NOT NULL,
    time_created                        TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified                       TIMESTAMP WITH TIME ZONE NOT NULL,
    trading_account_id                  BIGINT                   NOT NULL,
    group_id                            BIGINT                   NOT NULL,
    trade_type                          SMALLINT                 NOT NULL,
    executed_price                      NUMERIC                  NOT NULL,
    volume                              NUMERIC                  NOT NULL,
    order_id                            BIGINT                   NOT NULL,
    position_id                         BIGINT                   NOT NULL,
    profit                              NUMERIC                  NOT NULL,
    commission                          NUMERIC                  NOT NULL,
    swaps                               NUMERIC                  NOT NULL,
    fee                                 NUMERIC                  NOT NULL,
    position_price                      NUMERIC                  NOT NULL,
    profit_conversion_rate              NUMERIC                  NOT NULL,
    contract_size                       NUMERIC                  NOT NULL,
    notional_value_in_original_currency NUMERIC                  NOT NULL,
    notional_value_in_account_currency  NUMERIC                  NOT NULL,
    tick_value                          NUMERIC                  NOT NULL,
    tick_size                           NUMERIC                  NOT NULL,
    market_bid                          NUMERIC                  NOT NULL,
    market_ask                          NUMERIC                  NOT NULL,
    symbol_id                           BIGINT                   NOT NULL,
    group_markup                        NUMERIC                  NOT NULL,
    side                                SMALLINT                 NOT NULL,
    entry_type                          SMALLINT                 NOT NULL,
    reason                              SMALLINT                 NOT NULL,
    price_decimal_places                SMALLINT                 NOT NULL,
    account_currency_decimal_places     SMALLINT                 NOT NULL,
    comment                             VARCHAR(64)              NOT NULL DEFAULT '',
    agent_commission                    NUMERIC                  NOT NULL,
    notional_value_in_usd               NUMERIC                  NOT NULL
);

-- guards against storing a record twice
CREATE UNIQUE INDEX idx_sender_trade_trades ON trades (sender_id, trade_id);

-- time-range queries
CREATE INDEX idx_time_created_trades ON trades (time_created);

-- an account's records over a period; serves trades_history too
CREATE INDEX idx_account_time_trades ON trades (trading_account_id, time_created);

-- the same query against cash_transfers; partial, so it holds the cash movements only
CREATE INDEX idx_transfers_account_time_trades ON trades (trading_account_id, time_created)
    WHERE trade_type NOT IN (0, 1, 14, 15);
```

---

## Columns

Every column is `NOT NULL`. Amounts, prices and rates are unconstrained `NUMERIC`, so a value is stored exactly as
the trade server sent it — and a value that does not apply may be stored as `NaN`, see
[NaN, not NULL](#nan-not-null).

### Service columns

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Generated by PostgreSQL, not exported |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row was inserted; defaults to `now()`. Not when the trade happened — that is `time_created` |
| `sender_id` | `BIGINT` | The trade server instance that wrote the row, from the pipeline's `sender_id`. Each server exporting into one database must use its own value |

### Common columns — every record

| Column | Type | Description |
|--------|------|-------------|
| `group_name` | `VARCHAR(128)` | Name of the account's trading group, for example `Default` or `VIP`, as it was named when the record was written |
| `symbol_name` | `VARCHAR(32)` | Buy/Sell: the traded instrument, for example `EURUSD`. Non-trading: the currency of the operation — the account currency, for example `USD`, unless it is a collateral transfer |
| `trade_id` | `BIGINT` | The record's identifier in the trade server, unique per `sender_id` |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | When the record was created — for a Buy or Sell, the execution time |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | Always equals `time_created`: a record is immutable once created |
| `trading_account_id` | `BIGINT` | The trading account |
| `group_id` | `BIGINT` | The trading group |
| `trade_type` | `SMALLINT` | What kind of record this is — see [`trade_type`](#trade_type) |
| `entry_type` | `SMALLINT` | Buy/Sell: whether the trade opens, closes or reverses a position — see [`entry_type`](#entry_type) |
| `reason` | `SMALLINT` | Buy/Sell: what triggered the execution. Non-trading: `1` Admin, or `11` AgentCommission — see [`reason`](#reason) |
| `comment` | `VARCHAR(64)` | Free text; defaults to `''`. On a non-trading record it often carries the context, such as `Daily commission (2026-03-06)` or `AgentCommission for trade 12345` |
| `account_currency_decimal_places` | `SMALLINT` | Decimal places of the account currency |
| `notional_value_in_original_currency` | `NUMERIC` | Buy/Sell: the notional value in the instrument's base currency. Non-trading: the amount of the operation |
| `notional_value_in_account_currency` | `NUMERIC` | Buy/Sell: the notional value converted to the account currency. Non-trading: the amount in the account currency |

### Trading columns — Buy and Sell

On a non-trading record these hold a default — `0` or `NaN` — and should be ignored. The exception is a collateral
transfer, which fills seven of them from the symbol it was converted through; [`cash_transfers`](cash_transfers.md)
exposes exactly those.

| Column | Type | Description |
|--------|------|-------------|
| `executed_price` | `NUMERIC` | Price the trade executed at |
| `volume` | `NUMERIC` | Volume, in lots |
| `order_id` | `BIGINT` | The order that generated the trade |
| `position_id` | `BIGINT` | The position the trade belongs to. On a reversal (`entry_type` `2` InOut) the position it opened — there is no column for the position it closed |
| `profit` | `NUMERIC` | Realised profit or loss, in account currency, on a trade that closes a position (`Out`, `InOut`). `0` on an opening trade |
| `commission` | `NUMERIC` | Commission charged on the trade, in account currency |
| `swaps` | `NUMERIC` | Swap — overnight financing — in account currency, on a trade that closes a position |
| `fee` | `NUMERIC` | Fee charged on the trade, in account currency |
| `position_price` | `NUMERIC` | Average open price of the position the trade acted on — the price `profit` is measured from. On a reversal, that of the position it closed, whose volume has no column; on a trade that adds to a position, the average the fill produced |
| `profit_conversion_rate` | `NUMERIC` | Rate converting `profit` into the account currency |
| `contract_size` | `NUMERIC` | The instrument's contract size at execution, for example `100000` for a standard FX lot |
| `tick_value` | `NUMERIC` | Value of one tick of movement, in account currency, at execution |
| `tick_size` | `NUMERIC` | The instrument's minimum price increment, at execution |
| `market_bid` | `NUMERIC` | Market bid at execution |
| `market_ask` | `NUMERIC` | Market ask at execution |
| `symbol_id` | `BIGINT` | The instrument. `0` on a non-trading record other than a collateral transfer |
| `group_markup` | `NUMERIC` | Price markup applied by the trading group |
| `side` | `SMALLINT` | `0` Buy, `1` Sell |
| `price_decimal_places` | `SMALLINT` | Decimal places of the instrument's price |
| `agent_commission` | `NUMERIC` | Commission paid to the introducing broker or agent |
| `notional_value_in_usd` | `NUMERIC` | The notional value converted to USD |

The Admin REST API's `Trade` carries four things this table has no column for, so they cannot be read from the
export:

| API field | What it is |
|-----------|------------|
| `rpi` | The position a reversal closed. `position_id` is the one it opened |
| `cq` | The volume the trade closed — the volume `profit` was realised on: the trade's own volume on a close, the closed position's on a reversal, `0` on an opening trade |
| `mpb`, `mpa` | The market bid and ask, **with** the group markup, when the order was triggered or placed. `market_bid` and `market_ask` are the market prices at execution |
| `sp`, `lp` | The stop and limit price of the order that produced the trade |

---

## Values

The enum columns hold the trade server's ordinal, not the name. The values below are the ones the *Export Setup*
guide gives.

### `trade_type`

| Value | Name | Description | Read through |
|-------|------|-------------|--------------|
| `0` | Buy | Market buy execution | [`trades_history`](trades_history.md) |
| `1` | Sell | Market sell execution | [`trades_history`](trades_history.md) |
| `2` | Balance | Balance operation — deposit or withdrawal | [`cash_transfers`](cash_transfers.md) |
| `3` | Credit | Credit operation | `cash_transfers` |
| `4` | Fee | Fee charge | `cash_transfers` |
| `5` | Adjustment | Manual balance adjustment | `cash_transfers` |
| `6` | Bonus | Bonus credit | `cash_transfers` |
| `7` | Commission | Commission charge | `cash_transfers` |
| `8` | DailyCommission | Daily commission accrual | `cash_transfers` |
| `9` | MonthlyCommission | Monthly commission accrual | `cash_transfers` |
| `10` | AgentCommission | Agent / IB commission | `cash_transfers` |
| `11` | DailyAgentCommission | Daily agent commission accrual | `cash_transfers` |
| `12` | MonthlyAgentCommission | Monthly agent commission accrual | `cash_transfers` |
| `13` | Interest | Interest accrual | `cash_transfers` |
| `14` | BuyCancelled | Reserved — not currently generated | `trades_history` |
| `15` | SellCancelled | Reserved — not currently generated | `trades_history` |
| `16` | Dividend | Dividend payment | `cash_transfers` |
| `17` | FrankedDividend | Franked (tax-adjusted) dividend | `cash_transfers` |
| `18` | Tax | Tax charge | `cash_transfers` |
| `19` | StopoutCompensation | Compensation after a stop-out | `cash_transfers` |
| `20` | StopoutCreditAdjustment | Credit adjustment after a stop-out | `cash_transfers` |
| `21` | CreditBonus | Credit bonus | `cash_transfers` |

A type the trade server gains later is stored like any other value and surfaces in `cash_transfers`, whose filter is
the complement of the trades'.

### `entry_type`

| Value | Name | Description |
|-------|------|-------------|
| `0` | In | Opens a new position |
| `1` | Out | Closes an existing position |
| `2` | InOut | Reverses: closes the existing position and opens the opposite one |

> **Note:** On a non-trading record the *Export Setup* guide gives `entry_type` as always `0` In, while the
> [`cash_transfers`](cash_transfers.md#columns) source mapping, read from the trade server when the view was built,
> has the transfer pipeline write `In` or `Out` by the direction of the amount. Which one the current trade server
> writes is to be confirmed with its team; until then, tell a deposit from a withdrawal by the sign of the amount.

### `reason`

| Value | Name | Description |
|-------|------|-------------|
| `0` | Client | Executed by client request |
| `1` | Admin | Executed by an administrator |
| `2` | StopLoss | Triggered by a stop-loss order |
| `3` | TakeProfit | Triggered by a take-profit order |
| `4` | Stopout | Forced closure on insufficient margin |
| `5` | Rollover | Rollover of an expiring contract |
| `6` | CorporateAction | Triggered by a corporate action, such as a stock split |
| `7` | OrderExpired | A pending order expired |
| `8` | StopTriggered | A stop order was triggered |
| `9` | LimitTriggered | A limit order was triggered |
| `10` | ConnectorExecution | Executed by an external liquidity connector |
| `11` | AgentCommission | Agent commission trade |

A non-trading record always carries `1` Admin or `11` AgentCommission.

---

## NaN, not NULL

A `NUMERIC` column that does not apply to a record — the trading columns of a non-trading record, typically — may
hold the PostgreSQL numeric value `NaN` rather than `NULL`. Every column is `NOT NULL`, so `IS NULL` never finds it:

```sql
SELECT count(*) FROM trades WHERE profit IS NULL;   -- always 0
SELECT count(*) FROM trades WHERE profit = 'NaN';   -- the records without a profit
```

`NaN` is contagious in aggregates: one of them makes `sum()`, `avg()` and `max()` over the column `NaN`. Aggregate a
trading column through [`trades_history`](trades_history.md) — a Buy or Sell has every column populated — or
exclude `'NaN'` explicitly.

The snapshot tables are different: there the exporter writes `NULL` for a value that is not set — see
[`daily_statements_order_snapshots`](daily_statements_order_snapshots.md#columns).

---

## Indexes

| Index | Definition | Purpose |
|-------|------------|---------|
| `trades_pkey` | `PRIMARY KEY (id)` | |
| `idx_sender_trade_trades` | `UNIQUE (sender_id, trade_id)` | Guards against storing a record twice |
| `idx_time_created_trades` | `(time_created)` | Time-range queries |
| `idx_account_time_trades` | `(trading_account_id, time_created)` | An account's records over a period — the main reporting path, and `trades_history` |
| `idx_transfers_account_time_trades` | `(trading_account_id, time_created) WHERE trade_type NOT IN (0, 1, 14, 15)` | The same query against `cash_transfers`. Partial, so it holds the cash movements only and reads a fraction of the pages |

**The unique index is a guard, not a repair mechanism.** The exporter writes each batch with `COPY` in one
transaction, and PostgreSQL rejects the whole transaction when any row in it violates the index. The exporter
treats that as a permanent failure: it logs `failed to send batch ... dropping` and discards the **entire** batch —
the rows in it that were not duplicates included. A duplicate cannot corrupt the table, but a batch that contains
one is lost.

In normal operation it never fires: on every connection the exporter compares its position with
[`export_cursor`](export_cursor.md#what-the-exporter-does-on-connect) and skips past anything already committed.
The one case that reaches the index is a lost local state file, which the trade server logs as an `ERROR`
(`persistent state not loaded`).

What an index covers comes **before** the table name, because PostgreSQL truncates identifiers to 63 bytes: a long
configured table name would otherwise leave two indexes with the same truncated name, and the second
`CREATE INDEX IF NOT EXISTS` would quietly do nothing. The partial predicate is written exactly as the
`cash_transfers` view's `WHERE` clause, so the planner can match the two.

> **Warning:** The *Export Setup* guide names its two indexes `idx_trades_trade_id_sender_id` and
> `idx_trades_time_created`. On a table provisioned by hand from the guide, the migrator adds its own under the
> names above, so the table ends up with two identical unique indexes and two identical `time_created` ones. One of
> each pair is redundant and only costs writes — see
> [`IF NOT EXISTS` everywhere](../SCHEMA_REFERENCE.md#if-not-exists-everywhere).

> **Warning:** The two reporting indexes were added by an incremental script after the table existed. On a table
> already holding a serious number of rows, build them `CONCURRENTLY` by hand before migrating — see
> [Adding an index to a large, live table](../SCHEMA_REFERENCE.md#adding-an-index-to-a-large-live-table).

---

## Reading it

- Filter on `trading_account_id` and a `time_created` range — the shape of nearly every report, and the one the
  indexes serve. Pair the account with `sender_id` when more than one trade server exports into the database —
  see [Every id is scoped to its sender](../SCHEMA_REFERENCE.md#every-id-is-scoped-to-its-sender).
- `time_inserted` is when the row reached PostgreSQL. Use `time_created` for when the trade happened.
- The values that mattered at execution — `contract_size`, `tick_value`, `tick_size`, `price_decimal_places`,
  `group_name` — are on the row. They are not looked up from the symbol or group later, so they stay correct after
  the configuration changes.
- A non-trading record populates only part of the row; read it through [`cash_transfers`](cash_transfers.md), which
  exposes exactly the populated columns.

```sql
-- one account's trades over a month, newest first
SELECT trade_id, time_created, symbol_name, side, volume, executed_price, profit
FROM trades_history
WHERE sender_id = 1
  AND trading_account_id = 100042
  AND time_created >= '2026-09-01' AND time_created < '2026-10-01'
ORDER BY time_created DESC;
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20260101_01_create_trades.sql`](../../psql/20260101_01_create_trades.sql) | incremental | The table, `idx_sender_trade_trades`, `idx_time_created_trades` |
| [`psql/20260828_01_add_reporting_indexes.sql`](../../psql/20260828_01_add_reporting_indexes.sql) | incremental | `idx_account_time_trades`, `idx_transfers_account_time_trades` |
| [`psql/views/trades_history.sql`](../../psql/views/trades_history.sql), [`psql/views/cash_transfers.sql`](../../psql/views/cash_transfers.sql) | views | The two views over the table |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

Column descriptions and enum values follow the *Export Setup* guide's Trades section and the Admin REST API's
`Trade` and `Transfer`.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`trades_history`](trades_history.md) · [`cash_transfers`](cash_transfers.md) ·
[`export_cursor`](export_cursor.md)
