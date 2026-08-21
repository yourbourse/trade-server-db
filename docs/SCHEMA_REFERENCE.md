<div align="center">

# Schema Reference

**The tables the migrator creates, on both engines, column by column**

[Overview](#overview) · [trades](#trades) · [export_cursor](#export_cursor) · [schema_versions](#schema_versions) · [Type mapping](#type-mapping) · [Deduplication](#deduplication) · [Caveats](#caveats)

</div>

---

## Overview

Three tables, in the database named by the connection string:

| Table | Default name | Written by | Purpose |
|-------|--------------|------------|---------|
| Trades | `trades` | trade server | Every exported trade record, append-only |
| Export cursor | `export_cursor` | trade server | How far the export has progressed |
| Journal | `schema_versions` | migrator | Which scripts have been applied |

The first two names are configurable — they mirror the trade server's export configuration and are templated as
`##trades_table##` and `##cursor_table##`. The journal name comes from `--journal-table`.

Both providers carry the same logical schema, matching the *Export Setup* guide. Where the engines differ, the
difference is called out below.

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
| `idx_<trades_table>_sender_trade` | `UNIQUE (sender_id, trade_id)` | Deduplicates at-least-once delivery |
| `idx_<trades_table>_time_created` | `(time_created)` | Time-range queries |

### ClickHouse

Same columns and order, with two omissions and a few engine-specific choices:

| Difference | Detail |
|------------|--------|
| No `id` column | ClickHouse does not generate row ids, and the sorting key already identifies a row |
| No unique index | ClickHouse has no unique constraint — deduplication is the engine's job |
| `LowCardinality(String)` | Used for `group_name` and `symbol_name` |
| `INDEX idx_time_created … TYPE minmax GRANULARITY 4` | Data-skipping index, the counterpart of the PostgreSQL time index |

```sql
ENGINE = ReplacingMergeTree          -- ReplicatedReplacingMergeTree with --cluster
PARTITION BY toYYYYMM(time_created)
PRIMARY KEY (sender_id, trade_id)
ORDER BY (sender_id, trade_id)
SETTINGS index_granularity = 8192
```

Monthly partitions by `time_created` keep dropping old data cheap and let time-bounded queries skip whole
partitions.

---

## export_cursor

How many records the trade server has already committed for a given table. This table — not the trade server's
local `state.bin` — is the authoritative position; the local file is only a shutdown-time cache.

### PostgreSQL

| Column | Type | Notes |
|--------|------|-------|
| `sender_id` | `BIGINT` | Part of the primary key |
| `table_name` | `TEXT` | Part of the primary key |
| `last_written_position` | `BIGINT` | The committed position |

`PRIMARY KEY (sender_id, table_name)`.

The cursor row and the batch of trades are written **in the same transaction**, which is what makes the cursor
trustworthy: it can never claim more than was actually stored.

### ClickHouse

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
| `VARCHAR(n)` | `String` / `LowCardinality(String)` | ClickHouse does not enforce a length |
| `TEXT` | `String` | |
| `TIMESTAMP WITH TIME ZONE` | `DateTime64(6, 'UTC')` | Microsecond precision |
| `BIGINT GENERATED ALWAYS AS IDENTITY` | *(omitted)* | No ClickHouse equivalent |

---

## Deduplication

Export delivery is at-least-once: after a restart the trade server may resend records it already sent. The two
engines absorb that differently.

| | PostgreSQL | ClickHouse |
|---|------------|------------|
| Mechanism | `UNIQUE (sender_id, trade_id)` | `ReplacingMergeTree` sorted by `(sender_id, trade_id)` |
| When it applies | immediately, on insert | when parts merge — eventually |
| Effect of a duplicate | second insert rejected | both rows exist until the merge collapses them |
| Reading | always exact | add `FINAL` when duplicates must not be visible |

```sql
-- ClickHouse: exact counts before a merge has happened
SELECT count() FROM trades FINAL;
```

`FINAL` costs more than a plain read. For aggregate reporting over large ranges, consider deduplicating in the
query (`argMax` by `trade_id`) or accepting the small window of duplication instead.

---

## Caveats

### `NaN` on ClickHouse

`Decimal` cannot represent `NaN`. The *Export Setup* guide states that non-trading records — Balance, Credit,
Fee and the other accounting types — carry `0` or `NaN` in the fields that do not apply to them. An exporter
sending `NaN` gets `DECIMAL_OVERFLOW` and has to write `0` instead.

If the writer cannot be changed, switch the `Decimal(38, 18)` columns to `Float64`, accepting the loss of
exactness that implies for monetary values.

### `IF NOT EXISTS` everywhere

Every create statement is conditional, so the migrator can be pointed at a database that was provisioned by hand
from the guide before this repository existed. It adopts what is already there rather than failing — which also
means it will **not** correct a table whose columns differ from the script. Compare the schema by hand when
adopting a database you did not create.

### Renamed tables

If the trade server's export configuration uses non-default table names, every run must pass the same
`--variables trades_table=… cursor_table=…`. Nothing records the names on the database side, so a run without
them creates a second, empty set of tables under the default names.

---

## See also

[Writing Migrations](WRITING_MIGRATIONS.md) · [Users & Permissions](USERS_AND_PERMISSIONS.md) ·
[Template Variables](TEMPLATE_VARIABLES.md) · [Architecture](ARCHITECTURE.md)
