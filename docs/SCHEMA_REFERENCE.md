<div align="center">

# Schema Reference

**The tables the migrator creates, on both engines, column by column**

[Overview](#overview) · [trades](#trades) · [daily_statements_export](#daily_statements_export) · [order snapshots](#daily_statements_order_snapshots) · [position snapshots](#daily_statements_position_snapshots) · [export_cursor](#export_cursor) · [schema_versions](#schema_versions) · [Type mapping](#type-mapping) · [Deduplication](#deduplication) · [Caveats](#caveats)

</div>

---

## Overview

Six tables, in the database named by the connection string:

| Table | Default name | Written by | Purpose |
|-------|--------------|------------|---------|
| Trades | `trades` | trade server | Every exported trade record, append-only |
| Daily statements | `daily_statements_export` | trade server | One end-of-day row per account |
| Order snapshots | `daily_statements_order_snapshots` | trade server | The account's working orders at the statement time |
| Position snapshots | `daily_statements_position_snapshots` | trade server | The account's open positions at the statement time |
| Export cursor | `export_cursor` | trade server | How far the export has progressed |
| Journal | `schema_versions` | migrator | Which scripts have been applied |

The five names the trade server writes to are configurable — they mirror its export configuration and are
templated as `##trades_table##`, `##daily_statements_table##`, `##order_snapshots_table##`,
`##position_snapshots_table##` and `##cursor_table##`. The journal name comes from `--journal-table`.

The three daily statement tables are produced by one export: when a statement is generated the trade server
publishes the statement row, a snapshot of every **working** order and a snapshot of every open position, all
carrying the same scheduled end-of-day `time_statement`. That shared timestamp is what ties the three together —
there are no foreign keys.

```
statement run at time_statement
├── daily_statements_export             one row per account
├── daily_statements_order_snapshots    one row per working order
└── daily_statements_position_snapshots one row per open position
```

Each has its own pipeline in the trade server's `export_config.json`, so a deployment can export the statement
without the snapshots. Every pipeline shares the one `export_cursor` table, which holds a row per
`(sender_id, table_name)`.

> **Important:** **Which of these the migrator creates depends on the engine.**
>
> | | PostgreSQL | ClickHouse |
> |---|---|---|
> | The five tables the trade server writes to | created by the migrator | **not created** — provisioned by the deployment |
> | `schema_versions` | created by the migrator | created by the migrator |
> | Users and grants | applied by the migrator | applied by the migrator |
>
> On ClickHouse this repository grants on the exported tables **by name** without owning their definitions, which
> is why a grant succeeds even before the tables exist. The definitions below document what the migrator expects
> to find; the copies the integration tests migrate against live in
> [`src/Migrator.Tests/TestScripts/clickhouse/`](../src/Migrator.Tests/TestScripts/clickhouse) and are not part
> of a shipped migration.

Both engines carry the same logical schema, matching the *Export Setup* guide. Where they differ, the difference
is called out below.

---

## trades

Append-only. Delivery is at-least-once, so the same trade can arrive twice after a restart — see
[Deduplication](#deduplication).

### PostgreSQL

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Server-generated |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | Defaults to `now()` — when the row landed, not when the trade happened |
| `sender_id` | `BIGINT` | Identifies the writing trade server |
| `group_name` | `VARCHAR(128)` | |
| `symbol_name` | `VARCHAR(32)` | |
| `trade_id` | `BIGINT` | Unique per `sender_id` |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | When the trade was created |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | |
| `trading_account_id` | `BIGINT` | |
| `group_id` | `BIGINT` | |
| `trade_type` | `SMALLINT` | Trade / Balance / Credit / Fee and the other accounting types |
| `executed_price` | `NUMERIC` | |
| `volume` | `NUMERIC` | |
| `order_id` | `BIGINT` | |
| `position_id` | `BIGINT` | |
| `profit` | `NUMERIC` | |
| `commission` | `NUMERIC` | |
| `swaps` | `NUMERIC` | |
| `fee` | `NUMERIC` | |
| `position_price` | `NUMERIC` | |
| `profit_conversion_rate` | `NUMERIC` | |
| `contract_size` | `NUMERIC` | |
| `notional_value_in_original_currency` | `NUMERIC` | |
| `notional_value_in_account_currency` | `NUMERIC` | |
| `tick_value` | `NUMERIC` | |
| `tick_size` | `NUMERIC` | |
| `market_bid` | `NUMERIC` | |
| `market_ask` | `NUMERIC` | |
| `symbol_id` | `BIGINT` | |
| `group_markup` | `NUMERIC` | |
| `side` | `SMALLINT` | |
| `entry_type` | `SMALLINT` | |
| `reason` | `SMALLINT` | |
| `price_decimal_places` | `SMALLINT` | |
| `account_currency_decimal_places` | `SMALLINT` | |
| `comment` | `VARCHAR(64)` | Defaults to `''` |
| `agent_commission` | `NUMERIC` | |
| `notional_value_in_usd` | `NUMERIC` | |

Every column is `NOT NULL`.

**Indexes**

| Index | Definition | Purpose |
|-------|------------|---------|
| `idx_sender_trade_<trades_table>` | `UNIQUE (sender_id, trade_id)` | Deduplicates at-least-once delivery |
| `idx_time_created_<trades_table>` | `(time_created)` | Time-range queries |

What the index covers comes **before** the table name, because PostgreSQL truncates identifiers to 63 bytes: a
long configured table name would otherwise leave both indexes with the same truncated name, and the second
`CREATE INDEX IF NOT EXISTS` would quietly do nothing.

### ClickHouse

Not created by the migrator — the deployment provisions it. What the migrator expects, and what the integration
tests create, is the same column list with two omissions and a few engine-specific choices:

| Difference | Detail |
|------------|--------|
| No `id` column | ClickHouse does not generate row ids, and the sorting key already identifies a row |
| No unique index | ClickHouse has no unique constraint — deduplication is the engine's job |
| `LowCardinality(String)` | Used for `group_name` and `symbol_name` |
| `INDEX idx_time_created … TYPE minmax GRANULARITY 4` | Data-skipping index, the counterpart of the PostgreSQL time index |

```sql
ENGINE = ReplacingMergeTree          -- ReplicatedReplacingMergeTree with --cluster
PARTITION BY toYYYYMM(time_created)
PRIMARY KEY (sender_id)
ORDER BY (sender_id, trade_id, time_created)
SETTINGS index_granularity = 8192
```

Monthly partitions by `time_created` keep dropping old data cheap and let time-bounded queries skip whole
partitions.

The primary key and the sorting key are deliberately different lengths:

| Key | Contents | Why |
|-----|----------|-----|
| `PRIMARY KEY` | `(sender_id)` | This is the sparse index ClickHouse loads into memory. Keeping it to the low-cardinality column avoids holding a mark per trade id |
| `ORDER BY` | `(sender_id, trade_id, time_created)` | `ReplacingMergeTree` deduplicates on the **sorting** key, so `trade_id` has to stay in it — without it, two different trades sharing a sender and a timestamp would collapse into one |

The primary key must be a prefix of the sorting key, which is what makes this split legal.

---

## daily_statements_export

One row per account per statement run. Append-only, at-least-once, same as trades.

### PostgreSQL

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Server-generated |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | Defaults to `now()` — when the row landed |
| `sender_id` | `BIGINT` | Identifies the writing trade server |
| `time_statement` | `TIMESTAMP WITH TIME ZONE` | The scheduled end-of-day time. Shared with the snapshots of the same run |
| `group_name` | `VARCHAR(128)` | |
| `trading_account_id` | `BIGINT` | |
| `group_id` | `BIGINT` | |
| `statement_client_name` | `VARCHAR(160)` | Account owner |
| `account_currency` | `VARCHAR(16)` | `USD`, `EUR`, … |
| `email` | `VARCHAR(64)` | |
| `current_balance` | `NUMERIC` | |
| `prev_day_balance` | `NUMERIC` | |
| `current_credit` | `NUMERIC` | |
| `prev_day_credit` | `NUMERIC` | |
| `current_floating_profit` | `NUMERIC` | Unrealised, on open positions |
| `lifetime_realized_profit` | `NUMERIC` | Since the account was created |
| `current_equity` | `NUMERIC` | Balance plus floating profit |
| `prev_day_equity` | `NUMERIC` | |
| `current_margin` | `NUMERIC` | |
| `current_collateral` | `NUMERIC` | |
| `prev_day_collateral` | `NUMERIC` | |
| `open_positions_swaps` | `NUMERIC` | |
| `closed_positions_swaps` | `NUMERIC` | For the statement period |
| `commissions` | `NUMERIC` | |
| `agent_commissions` | `NUMERIC` | |
| `fees` | `NUMERIC` | |
| `interest` | `NUMERIC` | |
| `deposits` | `NUMERIC` | |
| `withdrawals` | `NUMERIC` | |
| `credit_transactions` | `NUMERIC` | |
| `adjustment_transactions` | `NUMERIC` | |
| `balance_bonus_transactions` | `NUMERIC` | |
| `credit_bonus_transactions` | `NUMERIC` | |
| `dividend_transactions` | `NUMERIC` | |
| `tax` | `NUMERIC` | |
| `stopout_compensations` | `NUMERIC` | |
| `stopout_credit_adjustments` | `NUMERIC` | |
| `volume_in_usd` | `NUMERIC` | |
| `volume_in_account_currency` | `NUMERIC` | |
| `realized_profit` | `NUMERIC` | For the statement period |
| `account_currency_decimal_places` | `SMALLINT` | Usually 2 |

Every column is `NOT NULL`.

**Indexes**

| Index | Definition | Purpose |
|-------|------------|---------|
| `idx_sender_stmt_account_<daily_statements_table>` | `UNIQUE (sender_id, time_statement, trading_account_id)` | Deduplicates at-least-once delivery |

### ClickHouse

Not created by the migrator — the deployment provisions it. What the migrator expects, and what the integration
tests create, is the same column list without `id`, with the types of the [mapping](#type-mapping) and
`LowCardinality(String)` on `group_name` and `account_currency`:

```sql
ENGINE = ReplacingMergeTree          -- ReplicatedReplacingMergeTree with --cluster
PARTITION BY toYYYYMM(time_statement)
PRIMARY KEY (sender_id)
ORDER BY (sender_id, time_statement, trading_account_id)
SETTINGS index_granularity = 8192
```

The sorting key is the PostgreSQL unique key, because that is what `ReplacingMergeTree` deduplicates on —
dropping `trading_account_id` from it would collapse every account of one statement run into a single row. The
primary key stays at `sender_id` alone for the same reason as on trades: it is the sparse index held in memory.

---

## daily_statements_order_snapshots

One row per **working** order of the account, as it stood at `time_statement`. A filled, cancelled or expired
order is not part of a statement.

### PostgreSQL

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Server-generated |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | Defaults to `now()` |
| `sender_id` | `BIGINT` | Identifies the writing trade server |
| `time_statement` | `TIMESTAMP WITH TIME ZONE` | Same value as on the statement row |
| `order_id` | `BIGINT` | Unique per `sender_id` and `time_statement` |
| `order_history_record_id` | `BIGINT` | Tracks order modifications |
| `trading_account_id` | `BIGINT` | |
| `group_id` | `BIGINT` | |
| `group_name` | `VARCHAR(128)` | |
| `symbol_name` | `VARCHAR(32)` | |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | |
| `classification_id` | `BIGINT` | |
| `symbol_id` | `BIGINT` | |
| `parent_order_id` | `BIGINT` | 0 when standalone |
| `oco_order_id` | `BIGINT` | 0 when not part of an OCO pair |
| `stop_loss_order_id` | `BIGINT` | 0 when none |
| `take_profit_order_id` | `BIGINT` | 0 when none |
| `parent_position_id` | `BIGINT` | 0 for a new position |
| `position_id` | `BIGINT` | 0 when the order opened no position |
| `position_by_id` | `BIGINT` | 0 when not applicable |
| `liquidity_connection_id` | `BIGINT` | 0 for a house order |
| `volume` | `NUMERIC` | In lots |
| `remaining_volume` | `NUMERIC` | Unfilled |
| `avg_fill_price` | `NUMERIC` **nullable** | `NULL` when unfilled |
| `stop_price` | `NUMERIC` **nullable** | `NULL` when not a stop order |
| `limit_price` | `NUMERIC` **nullable** | `NULL` when not applicable |
| `margin_conversion_rate` | `NUMERIC` | |
| `lot_size` | `NUMERIC` | Contract multiplier |
| `order_type` | `SMALLINT` | Market / Stop / Limit / Stop-Limit / Close By |
| `order_expiry` | `SMALLINT` | FOK / IOC / GTC / GTD / Day / Milliseconds |
| `order_side` | `SMALLINT` | 0 Buy, 1 Sell |
| `order_status` | `SMALLINT` | Only working orders are exported |
| `group_markup` | `NUMERIC` | |
| `price_decimal_places` | `SMALLINT` | |
| `account_currency_decimal_places` | `SMALLINT` | |
| `expiration_time` | `TIMESTAMP WITH TIME ZONE` **nullable** | `NULL` unless the order is Good Till Date |
| `reason` | `SMALLINT` | Same value set as on a trade |
| `comment` | `VARCHAR(64)` | |

**Indexes**

| Index | Definition | Purpose |
|-------|------------|---------|
| `idx_sender_stmt_order_<order_snapshots_table>` | `UNIQUE (sender_id, time_statement, order_id)` | Deduplicates at-least-once delivery |

### ClickHouse

Not created by the migrator — the deployment provisions it. In the definition the integration tests create, the
nullable columns become `Nullable(Decimal(38, 18))` and `Nullable(DateTime64(6, 'UTC'))`; `group_name` and
`symbol_name` are `LowCardinality(String)`:

```sql
ENGINE = ReplacingMergeTree          -- ReplicatedReplacingMergeTree with --cluster
PARTITION BY toYYYYMM(time_statement)
PRIMARY KEY (sender_id)
ORDER BY (sender_id, time_statement, order_id)
SETTINGS index_granularity = 8192
```

---

## daily_statements_position_snapshots

One row per open position of the account, as it stood at `time_statement`.

### PostgreSQL

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Server-generated |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | Defaults to `now()` |
| `sender_id` | `BIGINT` | Identifies the writing trade server |
| `time_statement` | `TIMESTAMP WITH TIME ZONE` | Same value as on the statement row |
| `position_id` | `BIGINT` | Unique per `sender_id` and `time_statement` |
| `trading_account_id` | `BIGINT` | |
| `group_id` | `BIGINT` | |
| `group_name` | `VARCHAR(128)` | |
| `symbol_name` | `VARCHAR(32)` | |
| `symbol_id` | `BIGINT` | |
| `side` | `SMALLINT` | 0 Buy (long), 1 Sell (short) |
| `volume` | `NUMERIC` | In lots |
| `avg_open_price` | `NUMERIC` | |
| `market_price` | `NUMERIC` | At snapshot time |
| `profit` | `NUMERIC` | Unrealised, in account currency |
| `swaps` | `NUMERIC` | |
| `commission` | `NUMERIC` | |
| `fee` | `NUMERIC` | |
| `margin` | `NUMERIC` | |
| `stop_loss` | `NUMERIC` **nullable** | `NULL` when not set |
| `stop_loss_order_id` | `BIGINT` | 0 when none |
| `take_profit` | `NUMERIC` **nullable** | `NULL` when not set |
| `take_profit_order_id` | `BIGINT` | 0 when none |
| `stopout_order_id` | `BIGINT` | 0 when no stop-out happened |
| `profit_conversion_rate` | `NUMERIC` | |
| `margin_conversion_rate` | `NUMERIC` | |
| `lot_size` | `NUMERIC` | Contract multiplier |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | When the position was opened |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | |
| `price_decimal_places` | `SMALLINT` | |
| `account_currency_decimal_places` | `SMALLINT` | |
| `intraday_swap_day` | `NUMERIC` | Accrued today |
| `intraday_swap_month` | `NUMERIC` | Accrued this month |
| `created_by_order_id` | `BIGINT` | |
| `updated_by_order_id` | `BIGINT` | Most recent order that modified the position |

**Indexes**

| Index | Definition | Purpose |
|-------|------------|---------|
| `idx_sender_stmt_position_<position_snapshots_table>` | `UNIQUE (sender_id, time_statement, position_id)` | Deduplicates at-least-once delivery |

### ClickHouse

Not created by the migrator — the deployment provisions it. In the definition the integration tests create,
`stop_loss` and `take_profit` become `Nullable(Decimal(38, 18))`:

```sql
ENGINE = ReplacingMergeTree          -- ReplicatedReplacingMergeTree with --cluster
PARTITION BY toYYYYMM(time_statement)
PRIMARY KEY (sender_id)
ORDER BY (sender_id, time_statement, position_id)
SETTINGS index_granularity = 8192
```

---

## export_cursor

How many records the trade server has already committed for a given table — one row per
`(sender_id, table_name)`, so every export pipeline of every server instance shares this one table. It — not the
trade server's local `state.bin` — is the authoritative position; the local file is only a shutdown-time cache.

### PostgreSQL

| Column | Type | Notes |
|--------|------|-------|
| `sender_id` | `BIGINT` | Part of the primary key |
| `table_name` | `TEXT` | Part of the primary key |
| `last_written_position` | `BIGINT` | The committed position |

`PRIMARY KEY (sender_id, table_name)`.

The cursor row and the batch of records are written **in the same transaction**, which is what makes the cursor
trustworthy: it can never claim more than was actually stored.

### ClickHouse

Not created by the migrator — as with the exported tables, the deployment provisions it and the migrator only
grants on it.

| Column | Type |
|--------|------|
| `sender_id` | `Int64` |
| `table_name` | `LowCardinality(String)` |
| `last_written_position` | `Int64` |

```sql
ENGINE = ReplacingMergeTree(last_written_position)
PRIMARY KEY (sender_id, table_name)
ORDER BY (sender_id, table_name)
```

ClickHouse has no row `UPDATE`, so the exporter **appends** a new position and the engine collapses the history.
`last_written_position` doubles as the version column, so the highest position always wins regardless of the
order parts merge in.

> **Note:** Read the ClickHouse cursor with `FINAL`, or an `argMax`, to get the current position — without it a
> query can see several rows for one `(sender_id, table_name)` until the parts merge.

```sql
SELECT sender_id, table_name, last_written_position FROM export_cursor FINAL;
```

---

## schema_versions

The migrator's own bookkeeping. Identical on both engines by design.

| Column | PostgreSQL | ClickHouse |
|--------|------------|------------|
| `script_name` | `TEXT NOT NULL`, primary key | `LowCardinality(String)` |
| `applied_at` | `TIMESTAMP WITH TIME ZONE NOT NULL` | `DateTime64(6, 'UTC')` |

ClickHouse uses `MergeTree` (or `ReplicatedMergeTree` with `--cluster`), ordered by
`(script_name, applied_at)`.

Entry naming:

| Kind | Journal entry | Re-runs when |
|------|---------------|--------------|
| Incremental | `[psql] 20260101_01_create_trades.sql` | never |
| Re-runnable | `[psql/post] run_on_every_build.sql::a1b2…` | its processed content changes |
| Users | `[psql/users] exporter.sql for trade_exporter::7f3c…` | its content, name or password changes |

The `::` suffix is the MD5 of the processed script. Before writing a new entry the migrator deletes every
previous entry for the same script, so exactly one row per re-runnable script survives.

```sql
SELECT script_name, applied_at FROM schema_versions ORDER BY applied_at;
```

---

## Type mapping

The guide's PostgreSQL types map onto ClickHouse as:

| PostgreSQL | ClickHouse | Note |
|------------|------------|------|
| `BIGINT` | `Int64` | |
| `SMALLINT` | `Int16` | |
| `NUMERIC` | `Decimal(38, 18)` | 38 significant digits, 18 after the point |
| `NUMERIC` nullable | `Nullable(Decimal(38, 18))` | The snapshot prices that are `NULL` when unset |
| `VARCHAR(n)` | `String` / `LowCardinality(String)` | ClickHouse does not enforce a length |
| `TEXT` | `String` | |
| `TIMESTAMP WITH TIME ZONE` | `DateTime64(6, 'UTC')` | Microsecond precision |
| `TIMESTAMP WITH TIME ZONE` nullable | `Nullable(DateTime64(6, 'UTC'))` | `expiration_time` on an order snapshot |
| `BIGINT GENERATED ALWAYS AS IDENTITY` | *(omitted)* | No ClickHouse equivalent |

---

## Deduplication

Export delivery is at-least-once: after a restart the trade server may resend records it already sent. The two
engines absorb that differently.

Each table has its own key, and on both engines the same columns carry the deduplication:

| Table | Key |
|-------|-----|
| Trades | `(sender_id, trade_id)` |
| Daily statements | `(sender_id, time_statement, trading_account_id)` |
| Order snapshots | `(sender_id, time_statement, order_id)` |
| Position snapshots | `(sender_id, time_statement, position_id)` |

| | PostgreSQL | ClickHouse |
|---|------------|------------|
| Mechanism | a `UNIQUE` index on the key | `ReplacingMergeTree` sorted by the key |
| When it applies | immediately, on insert | when parts merge — eventually |
| Effect of a duplicate | second insert rejected | both rows exist until the merge collapses them |
| Reading | always exact | add `FINAL` when duplicates must not be visible |

```sql
-- ClickHouse: exact counts before a merge has happened
SELECT count() FROM trades FINAL;
```

`FINAL` costs more than a plain read. For aggregate reporting over large ranges, consider deduplicating in the
query (`argMax` by the key) or accepting the small window of duplication instead.

---

## Caveats

### `NaN` on ClickHouse

`Decimal` cannot represent `NaN`. The *Export Setup* guide states that non-trading records — Balance, Credit,
Fee and the other accounting types — carry `0` or `NaN` in the fields that do not apply to them. An exporter
sending `NaN` gets `DECIMAL_OVERFLOW` and has to write `0` instead.

If the writer cannot be changed, switch the `Decimal(38, 18)` columns to `Float64`, accepting the loss of
exactness that implies for monetary values.

The snapshot tables are the exception, and only for the columns the guide marks nullable — `avg_fill_price`,
`stop_price`, `limit_price`, `stop_loss` and `take_profit`. There a `NaN` means *not set*, so the exporter writes
`NULL` and the column is nullable on both engines.

### `IF NOT EXISTS` everywhere

Every create statement is conditional, so the migrator can be pointed at a database that was provisioned by hand
from the guide before this repository existed. It adopts what is already there rather than failing — which also
means it will **not** correct a table whose columns differ from the script. Compare the schema by hand when
adopting a database you did not create.

### The ClickHouse tables have to exist before the export runs

Because the migrator does not create them, provisioning a ClickHouse target is two steps: create the five tables
above — the four exported ones and the cursor — then run the migrator to apply the journal, the users and the
`post/` phase. The grants themselves succeed either way — ClickHouse accepts a `GRANT` naming a table that does
not exist yet — so a missing table shows up as a failing export, not a failing migration.

### Renamed tables

If the trade server's export configuration uses non-default table names, every run must pass the same
`--variables trades_table=… daily_statements_table=… order_snapshots_table=… position_snapshots_table=…
cursor_table=…`. Nothing records the names on the database side, so a run without them grants on — and on
PostgreSQL creates — a second set of tables under the default names.

---

## See also

[Writing Migrations](WRITING_MIGRATIONS.md) · [Users & Permissions](USERS_AND_PERMISSIONS.md) ·
[Template Variables](TEMPLATE_VARIABLES.md) · [Architecture](ARCHITECTURE.md)
