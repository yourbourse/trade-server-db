<div align="center">

# daily_statements_export

**One end-of-day statement per account per statement run — balances, equity, margin and the day's totals**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Indexes](#indexes) · [The statement family](#the-statement-family) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `daily_statements_export` — renamed with `--variables daily_statements_table=…` |
| Written by | The trade server's `daily_statement` pipeline — `"entity": "daily_statement"`, `"target": "postgresql"` |
| One row per | Account per end-of-day run |
| Deduplicated by | `UNIQUE (sender_id, time_statement, trading_account_id)` — a guard; see [Indexes](#indexes) |
| Grants | `exporter`: `INSERT`, `SELECT` · `ro`: `SELECT` |
| Related | [`daily_statements_order_snapshots`](daily_statements_order_snapshots.md), [`daily_statements_position_snapshots`](daily_statements_position_snapshots.md) |

The end-of-day account summary: balance, equity, margin, P&L, swaps, commissions, the period's transactions and
traded volume, as they stood at the scheduled end-of-day time carried in `time_statement`. Only **daily** statements
are exported — there are no monthly ones here.

---

## Final SQL

The table as it stands once both incremental scripts under [Scripts](#scripts) have run, with the default names
substituted. On an empty database it creates the same table, indexes and constraints the migrator does.

```sql
CREATE TABLE daily_statements_export
(
    id                              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                       BIGINT                   NOT NULL,
    time_statement                  TIMESTAMP WITH TIME ZONE NOT NULL,
    group_name                      VARCHAR(128)             NOT NULL,
    trading_account_id              BIGINT                   NOT NULL,
    group_id                        BIGINT                   NOT NULL,
    statement_client_name           VARCHAR(160)             NOT NULL,
    account_currency                VARCHAR(16)              NOT NULL,
    email                           VARCHAR(64)              NOT NULL,
    current_balance                 NUMERIC                  NOT NULL,
    prev_day_balance                NUMERIC                  NOT NULL,
    current_credit                  NUMERIC                  NOT NULL,
    prev_day_credit                 NUMERIC                  NOT NULL,
    current_floating_profit         NUMERIC                  NOT NULL,
    lifetime_realized_profit        NUMERIC                  NOT NULL,
    current_equity                  NUMERIC                  NOT NULL,
    prev_day_equity                 NUMERIC                  NOT NULL,
    current_margin                  NUMERIC                  NOT NULL,
    current_collateral              NUMERIC                  NOT NULL,
    prev_day_collateral             NUMERIC                  NOT NULL,
    open_positions_swaps            NUMERIC                  NOT NULL,
    closed_positions_swaps          NUMERIC                  NOT NULL,
    commissions                     NUMERIC                  NOT NULL,
    agent_commissions               NUMERIC                  NOT NULL,
    fees                            NUMERIC                  NOT NULL,
    interest                        NUMERIC                  NOT NULL,
    deposits                        NUMERIC                  NOT NULL,
    withdrawals                     NUMERIC                  NOT NULL,
    credit_transactions             NUMERIC                  NOT NULL,
    adjustment_transactions         NUMERIC                  NOT NULL,
    balance_bonus_transactions      NUMERIC                  NOT NULL,
    credit_bonus_transactions       NUMERIC                  NOT NULL,
    dividend_transactions           NUMERIC                  NOT NULL,
    tax                             NUMERIC                  NOT NULL,
    stopout_compensations           NUMERIC                  NOT NULL,
    stopout_credit_adjustments      NUMERIC                  NOT NULL,
    volume_in_usd                   NUMERIC                  NOT NULL,
    volume_in_account_currency      NUMERIC                  NOT NULL,
    realized_profit                 NUMERIC                  NOT NULL,
    account_currency_decimal_places SMALLINT                 NOT NULL
);

-- guards against storing a statement twice
CREATE UNIQUE INDEX idx_sender_stmt_account_daily_statements_export
    ON daily_statements_export (sender_id, time_statement, trading_account_id);

-- an account's statement history
CREATE INDEX idx_account_stmt_daily_statements_export
    ON daily_statements_export (trading_account_id, time_statement);
```

---

## Columns

Three service columns and 38 data columns, every one `NOT NULL`. The amounts are in the account currency, and a
"period" figure covers the day the statement closes. The data columns follow the Admin REST API's `Statement` — what
`POST /admin/statements/query` returns — and the descriptions are the API's.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Generated by PostgreSQL, not exported |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row was inserted; defaults to `now()` |
| `sender_id` | `BIGINT` | The trade server instance that wrote the row, from the pipeline's `sender_id` |
| `time_statement` | `TIMESTAMP WITH TIME ZONE` | The statement date: the scheduled end-of-day time. Shared with the order and position snapshots of the same run |
| **Account** | | |
| `group_name` | `VARCHAR(128)` | Name of the account's group |
| `trading_account_id` | `BIGINT` | The trading account |
| `group_id` | `BIGINT` | The account's group |
| `statement_client_name` | `VARCHAR(160)` | The client's name |
| `account_currency` | `VARCHAR(16)` | The account currency, such as `USD` or `EUR` |
| `email` | `VARCHAR(64)` | The account holder's email address |
| **Balance, credit, equity** | | |
| `current_balance` | `NUMERIC` | Account balance |
| `prev_day_balance` | `NUMERIC` | The previous day's balance |
| `current_credit` | `NUMERIC` | Account credit |
| `prev_day_credit` | `NUMERIC` | The previous day's credit |
| `current_floating_profit` | `NUMERIC` | Unrealised profit or loss on the open positions |
| `lifetime_realized_profit` | `NUMERIC` | Realised profit or loss since the account was created — profit plus swaps, on the same basis as `realized_profit` |
| `current_equity` | `NUMERIC` | Equity: balance + credit + unrealised profit or loss + collateral |
| `prev_day_equity` | `NUMERIC` | The previous day's equity |
| **Margin and collateral** | | |
| `current_margin` | `NUMERIC` | The account's margin requirement |
| `current_collateral` | `NUMERIC` | Market value of the collateral, adjusted by the liquidity margin rate |
| `prev_day_collateral` | `NUMERIC` | The same, the previous day |
| **Swaps** | | |
| `open_positions_swaps` | `NUMERIC` | Swaps on the positions still open |
| `closed_positions_swaps` | `NUMERIC` | Swaps on the positions closed during the period |
| **The period's charges and transactions** | | |
| `commissions` | `NUMERIC` | Commissions charged during the period |
| `agent_commissions` | `NUMERIC` | Agent commissions paid during the period |
| `fees` | `NUMERIC` | Fees charged during the period |
| `interest` | `NUMERIC` | Interest debited or credited during the period |
| `deposits` | `NUMERIC` | Total deposits made during the period — a **positive** amount |
| `withdrawals` | `NUMERIC` | Total withdrawals made during the period — a **negative** amount |
| `credit_transactions` | `NUMERIC` | Net amount of the credit transactions of the period |
| `adjustment_transactions` | `NUMERIC` | Net amount of the adjustment transactions |
| `balance_bonus_transactions` | `NUMERIC` | Net amount of the balance bonus transactions |
| `credit_bonus_transactions` | `NUMERIC` | Net amount of the credit bonus transactions |
| `dividend_transactions` | `NUMERIC` | Net amount of the dividend transactions |
| `tax` | `NUMERIC` | Net amount of tax paid |
| `stopout_compensations` | `NUMERIC` | Net amount of the stop-out compensation transactions |
| `stopout_credit_adjustments` | `NUMERIC` | Net amount of the stop-out credit adjustment transactions |
| **Volume, profit, precision** | | |
| `volume_in_usd` | `NUMERIC` | Total trading volume during the period, in USD |
| `volume_in_account_currency` | `NUMERIC` | Total trading volume during the period, in the account currency |
| `realized_profit` | `NUMERIC` | Realised profit or loss for the period — profit plus swaps on the volume closed in the period. Commissions and fees are not in it; they are in `commissions` and `fees` |
| `account_currency_decimal_places` | `SMALLINT` | Decimal places of the account currency, typically 2. Added by the export — the API's statement does not carry it |

`withdrawals` is negative, so the period's net cash flow is `deposits + withdrawals`. The API's statement also
carries the previous **month's** balance, credit, equity and collateral; the export does not — only daily statements
are exported.

---

## Indexes

| Index | Definition | Purpose |
|-------|------------|---------|
| `daily_statements_export_pkey` | `PRIMARY KEY (id)` | |
| `idx_sender_stmt_account_daily_statements_export` | `UNIQUE (sender_id, time_statement, trading_account_id)` | Each account's statement at most once per end-of-day time |
| `idx_account_stmt_daily_statements_export` | `(trading_account_id, time_statement)` | An account's statement history. The unique index carries `trading_account_id` third, so it cannot serve a filter on the account alone |

As on [`trades`](trades.md#indexes), the unique index is a guard: the export cursor keeps a statement from being
written twice in normal operation, and a batch that does hit the index is rejected whole and dropped by the
exporter, new rows included. The *Export Setup* guide names its version `idx_daily_statements_composite_key`; on a
table provisioned by hand from the guide the migrator adds its own beside it.

`stmt` in the index names keeps them inside PostgreSQL's 63-byte identifier limit with the default table names —
the statement tables have the longest names in the schema.

---

## The statement family

The three daily statement tables are produced by the same end-of-day run. When a statement is generated the trade
server publishes the statement row, a snapshot of every **working** order and a snapshot of every open position, all
carrying the same scheduled end-of-day `time_statement`. That shared timestamp is what ties them together — there
are no foreign keys — so a statement and its orders and positions join on
`(sender_id, time_statement, trading_account_id)`.

```
statement run at time_statement
├── daily_statements_export             one row per account
├── daily_statements_order_snapshots    one row per working order
└── daily_statements_position_snapshots one row per open position
```

Each is exported by its own pipeline in `export_config.json`, so a deployment can export any subset of the three.
They arrive as one large burst once a day rather than as a steady trickle.

```sql
-- one account's statement and the open positions behind it
SELECT s.time_statement, s.current_balance, s.current_equity, p.symbol_name, p.volume, p.profit
FROM daily_statements_export s
         LEFT JOIN daily_statements_position_snapshots p
                   ON p.sender_id = s.sender_id
                       AND p.time_statement = s.time_statement
                       AND p.trading_account_id = s.trading_account_id
WHERE s.sender_id = 1
  AND s.trading_account_id = 100042
  AND s.time_statement = '2026-09-30 21:00:00+00';
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20260101_03_create_daily_statements.sql`](../../psql/20260101_03_create_daily_statements.sql) | incremental | The table, `idx_sender_stmt_account_daily_statements_export` |
| [`psql/20260828_01_add_reporting_indexes.sql`](../../psql/20260828_01_add_reporting_indexes.sql) | incremental | `idx_account_stmt_daily_statements_export` |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

Column descriptions follow the Admin REST API's `Statement` and the *Export Setup* guide's Daily
Statements section.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`daily_statements_order_snapshots`](daily_statements_order_snapshots.md) ·
[`daily_statements_position_snapshots`](daily_statements_position_snapshots.md)
