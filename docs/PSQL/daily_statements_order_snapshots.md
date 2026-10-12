<div align="center">

# daily_statements_order_snapshots

**Every working order of an account as it stood at the statement time — one row per order per statement run**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Values](#values) · [Indexes](#indexes) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `daily_statements_order_snapshots` — renamed with `--variables order_snapshots_table=…` |
| Written by | The trade server's `daily_statement_order_snapshot` pipeline — `"entity": "daily_statement_order_snapshot"`, `"target": "postgresql"` |
| One row per | Order the account still holds when its daily statement is generated |
| Deduplicated by | `UNIQUE (sender_id, time_statement, order_id)` — a guard, as on [`trades`](trades.md#indexes) |
| Grants | `exporter`: `INSERT`, `SELECT` · `ro`: `SELECT` |
| Related | [`daily_statements_export`](daily_statements_export.md) — the statement it belongs to |

Each row captures the order's state at the moment the statement was generated and carries the statement's
`time_statement`, so orders join to their statement on `(sender_id, time_statement, trading_account_id)` — see
[the statement family](daily_statements_export.md#the-statement-family).

Only **working** orders are included, in the broad sense of not yet final — see [`order_status`](#order_status). An
order that has already reached a final state — filled, cancelled, rejected or expired — is not part of the snapshot.

---

## Final SQL

The table as it stands once both incremental scripts under [Scripts](#scripts) have run, with the default names
substituted. On an empty database it creates the same table, indexes and constraints the migrator does.

```sql
CREATE TABLE daily_statements_order_snapshots
(
    id                              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                       BIGINT                   NOT NULL,
    time_statement                  TIMESTAMP WITH TIME ZONE NOT NULL,
    order_id                        BIGINT                   NOT NULL,
    order_history_record_id         BIGINT                   NOT NULL,
    trading_account_id              BIGINT                   NOT NULL,
    group_id                        BIGINT                   NOT NULL,
    group_name                      VARCHAR(128)             NOT NULL,
    symbol_name                     VARCHAR(32)              NOT NULL,
    time_created                    TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified                   TIMESTAMP WITH TIME ZONE NOT NULL,
    classification_id               BIGINT                   NOT NULL,
    symbol_id                       BIGINT                   NOT NULL,
    parent_order_id                 BIGINT                   NOT NULL,
    oco_order_id                    BIGINT                   NOT NULL,
    stop_loss_order_id              BIGINT                   NOT NULL,
    take_profit_order_id            BIGINT                   NOT NULL,
    parent_position_id              BIGINT                   NOT NULL,
    position_id                     BIGINT                   NOT NULL,
    position_by_id                  BIGINT                   NOT NULL,
    liquidity_connection_id         BIGINT                   NOT NULL,
    volume                          NUMERIC                  NOT NULL,
    remaining_volume                NUMERIC                  NOT NULL,
    avg_fill_price                  NUMERIC,
    stop_price                      NUMERIC,
    limit_price                     NUMERIC,
    margin_conversion_rate          NUMERIC                  NOT NULL,
    lot_size                        NUMERIC                  NOT NULL,
    order_type                      SMALLINT                 NOT NULL,
    order_expiry                    SMALLINT                 NOT NULL,
    order_side                      SMALLINT                 NOT NULL,
    order_status                    SMALLINT                 NOT NULL,
    group_markup                    NUMERIC                  NOT NULL,
    price_decimal_places            SMALLINT                 NOT NULL,
    account_currency_decimal_places SMALLINT                 NOT NULL,
    expiration_time                 TIMESTAMP WITH TIME ZONE,
    reason                          SMALLINT                 NOT NULL,
    comment                         VARCHAR(64)              NOT NULL
);

-- guards against storing an order twice per statement
CREATE UNIQUE INDEX idx_sender_stmt_order_daily_statements_order_snapshots
    ON daily_statements_order_snapshots (sender_id, time_statement, order_id);

-- the working orders behind one account's statement
CREATE INDEX idx_account_stmt_daily_statements_order_snapshots
    ON daily_statements_order_snapshots (trading_account_id, time_statement);
```

---

## Columns

Three service columns and 36 data columns. Every column is `NOT NULL` except the four marked nullable.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Generated by PostgreSQL, not exported |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row was inserted; defaults to `now()` |
| `sender_id` | `BIGINT` | The trade server instance that wrote the row, from the pipeline's `sender_id` |
| `time_statement` | `TIMESTAMP WITH TIME ZONE` | The scheduled end-of-day time. Every order of one statement shares it, and so does the statement row |
| **Identifiers** | | |
| `order_id` | `BIGINT` | The order. Unique per `sender_id` and `time_statement` |
| `order_history_record_id` | `BIGINT` | The order's history record — tracks its modifications |
| `trading_account_id` | `BIGINT` | The account the order belongs to |
| `group_id` | `BIGINT` | The account's group |
| `group_name` | `VARCHAR(128)` | Name of the account's group |
| `symbol_name` | `VARCHAR(32)` | The instrument, such as `EURUSD` or `AAPL` |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | When the order was created |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | When the order was last modified |
| `classification_id` | `BIGINT` | Order classification — an internal categorisation |
| `symbol_id` | `BIGINT` | The instrument's identifier |
| **Related orders and positions** | | |
| `parent_order_id` | `BIGINT` | The parent order — set on a bracket order, the stop loss or take profit attached to a base order. `0` when the order is standalone |
| `oco_order_id` | `BIGINT` | The other order of a One-Cancels-Other pair. `0` when not part of one |
| `stop_loss_order_id` | `BIGINT` | The order's stop-loss order, when a stop loss is set. `0` when none |
| `take_profit_order_id` | `BIGINT` | The order's take-profit order, when a take profit is set. `0` when none |
| `parent_position_id` | `BIGINT` | The existing position the order acts on — set on a bracket order for a position. `0` for an order that opens a new position |
| `position_id` | `BIGINT` | The position resulting from the order. `0` when it opened or modified none |
| `position_by_id` | `BIGINT` | An alternative position reference. `0` when it does not apply |
| `liquidity_connection_id` | `BIGINT` | The liquidity provider connection. `0` for an internal (house) order |
| **Volume and prices** | | |
| `volume` | `NUMERIC` | The order quantity, in lots |
| `remaining_volume` | `NUMERIC` | The unfilled quantity, in lots: `volume` less what has filled. Never `0` here — a filled order is not snapshotted |
| `avg_fill_price` | `NUMERIC`, **nullable** | Average price of the order's fills. `NULL` when nothing has filled |
| `stop_price` | `NUMERIC`, **nullable** | Stop price, on a stop or stop-limit order. `NULL` otherwise |
| `limit_price` | `NUMERIC`, **nullable** | Limit price, on a limit or stop-limit order. `NULL` otherwise |
| `margin_conversion_rate` | `NUMERIC` | Rate applied to the margin calculation |
| `lot_size` | `NUMERIC` | Contract multiplier, such as `100000` for an FX pair |
| **Order settings** | | |
| `order_type` | `SMALLINT` | See [`order_type`](#order_type) |
| `order_expiry` | `SMALLINT` | See [`order_expiry`](#order_expiry) |
| `order_side` | `SMALLINT` | `0` Buy, `1` Sell |
| `order_status` | `SMALLINT` | See [`order_status`](#order_status) |
| `group_markup` | `NUMERIC` | Markup the group applies to this order |
| `price_decimal_places` | `SMALLINT` | Decimal places of the instrument's price |
| `account_currency_decimal_places` | `SMALLINT` | Decimal places of the account currency |
| `expiration_time` | `TIMESTAMP WITH TIME ZONE`, **nullable** | When a Good Till Date order expires. `NULL` unless the order is GTD |
| `reason` | `SMALLINT` | Why the order was created or modified — the same values as on a trade, see [`reason`](trades.md#reason) |
| `comment` | `VARCHAR(64)` | Comment on the order |

**`NULL`, not `NaN`.** A price that is not set is `NaN` in the trade server, and on this table the exporter writes it
as SQL `NULL`, so an ordinary `IS NULL` finds it. That is specific to the snapshot tables: in
[`trades`](trades.md#nan-not-null) a value that does not apply is stored as `NaN`.

---

## Values

### `order_type`

| Value | Name |
|-------|------|
| `0` | Market |
| `1` | Stop |
| `2` | Limit |
| `3` | Stop-Limit |
| `4` | Close By |

### `order_expiry`

The order's time in force.

| Value | Name | Description |
|-------|------|-------------|
| `0` | FOK | Fill or Kill |
| `1` | IOC | Immediate or Cancel |
| `2` | GTC | Good Till Cancel |
| `3` | GTD | Good Till Date — the only one with an `expiration_time` |
| `4` | Day | Valid for the day |
| `5` | Milliseconds | Valid for a number of milliseconds after it was placed |

A Market or Close By order takes only FOK or IOC.

### `order_status`

| Value | Name | Description |
|-------|------|-------------|
| `0` | Inactive | A stop-loss or take-profit order created but waiting for its base order to be filled |
| `1` | Working | Created and active, not executed yet |
| `2` | PartiallyFilled | Partially filled and still active |
| `3` | Filled | Fully filled |
| `4` | Cancelled | Cancelled |
| `5` | Rejected | Rejected |
| `6` | Expired | Expired |

A final state — `3` to `6` — never appears here, since such an order is not part of a snapshot.

---

## Indexes

| Index | Definition | Purpose |
|-------|------------|---------|
| `daily_statements_order_snapshots_pkey` | `PRIMARY KEY (id)` | |
| `idx_sender_stmt_order_daily_statements_order_snapshots` | `UNIQUE (sender_id, time_statement, order_id)` | Each order at most once per end-of-day time |
| `idx_account_stmt_daily_statements_order_snapshots` | `(trading_account_id, time_statement)` | The working orders behind one account's statement. `trading_account_id` is not in the unique index at all |

The *Export Setup* guide names its unique index `idx_order_snapshots_composite_key`; on a table provisioned by hand
from the guide the migrator adds its own beside it.

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20260101_04_create_order_snapshots.sql`](../../psql/20260101_04_create_order_snapshots.sql) | incremental | The table, `idx_sender_stmt_order_daily_statements_order_snapshots` |
| [`psql/20260828_01_add_reporting_indexes.sql`](../../psql/20260828_01_add_reporting_indexes.sql) | incremental | `idx_account_stmt_daily_statements_order_snapshots` |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

Column descriptions and enum values follow the *Export Setup* guide's Daily Order Snapshots section and the Admin
REST API's `Order`.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`daily_statements_export`](daily_statements_export.md) ·
[`daily_statements_position_snapshots`](daily_statements_position_snapshots.md)
