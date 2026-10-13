<div align="center">

# Schema Reference

**The source of truth for the export databases — every table and view the migrator creates, one page each**

[Overview](#overview) · [PostgreSQL](#postgresql) · [ClickHouse](#clickhouse) · [Conventions](#conventions) · [Event logs](#event-logs-and-their-projections) · [Deduplication](#deduplication) · [Caveats](#caveats)

</div>

---

## Overview

The trade server exports each entity to exactly one engine, so the two engines hold different tables. Every one of
them is created by the migrator, in the database named by the connection string, and each has its own page — under
`PSQL/` for [PostgreSQL](#postgresql) and `ClickHouse/` for [ClickHouse](#clickhouse) — named after its default table
name.

Every page has the same sections:

| Section | What it holds |
|---------|---------------|
| **Overview** | The engine, the default name and the variable that renames it, who writes it, what one row is, its key and its grants |
| **Final SQL** | The table as it stands once **every** script has run, with the default names substituted — a single definition even when several scripts built it, such as a table and the indexes a later migration added |
| **Columns** | Every column, its type and what it means |
| **Indexes** | What each index covers and which query it serves |
| **Scripts** | Which scripts built the object, in which phase, and what each contributed |

The **Final SQL** is checked against the migrator, not written from memory: applied to an empty database it creates
the same tables, columns, defaults, constraints, indexes and views a migration creates. When a migration changes a
table, its page changes in the same pull request — see [Contributing](CONTRIBUTING.md#review-checklist).

The column descriptions come from two documents of the trade server team: the *Export Setup* guide, which describes
what the exporter writes, and the trade server's Admin REST API, whose `TradingAccount`, `Client`, `Group`,
`Symbol`, `Order`, `Position`, `Trade`, `Transfer` and `Statement` resources carry the same data. Where the two
disagree, the page says so.

> **Important:** **The engines are not mirrors of each other.** The *Export Setup* guide sends the trades, the daily
> statements and their snapshots, the event logs and the cursor to PostgreSQL only, and the ticks to ClickHouse
> only; a pipeline that pairs an entity with the other engine is rejected by the trade server. So `psql/` creates
> the PostgreSQL tables, their projections and views, `clickhouse/` creates the ticks table and nothing else, and
> both create the journal.

---

## PostgreSQL

### Exported by the trade server

Each table is written by one pipeline of the trade server's `export_config.json`, identified by its `entity`. The
pipeline's `table` field names the table; a deployment that changed it passes the same name to every migration, as
`--variables <variable>=<name>` — see [Renamed tables](#renamed-tables).

| Entity | Page | Pipeline `entity` | Default name | Variable | One row per |
|--------|------|-------------------|--------------|----------|-------------|
| Export cursor | [export_cursor](PSQL/export_cursor.md) | every pipeline's `cursor_table` | `export_cursor` | `cursor_table` | PostgreSQL pipeline and sender |
| Trades | [trades](PSQL/trades.md) | `trade` | `trades` | `trades_table` | Trade or cash movement |
| Daily statements | [daily_statements_export](PSQL/daily_statements_export.md) | `daily_statement` | `daily_statements_export` | `daily_statements_table` | Account per statement run |
| Daily order snapshots | [daily_statements_order_snapshots](PSQL/daily_statements_order_snapshots.md) | `daily_statement_order_snapshot` | `daily_statements_order_snapshots` | `order_snapshots_table` | Working order per statement run |
| Daily position snapshots | [daily_statements_position_snapshots](PSQL/daily_statements_position_snapshots.md) | `daily_statement_position_snapshot` | `daily_statements_position_snapshots` | `position_snapshots_table` | Open position per statement run |
| Trading account events | [trading_account_events](PSQL/trading_account_events.md) | `trading_account` | `trading_account_events` | `trading_account_events_table` | Trading account change or resync marker |
| Client events | [client_events](PSQL/client_events.md) | `client` | `client_events` | `client_events_table` | Client change or resync marker |
| Symbol events | [symbol_events](PSQL/symbol_events.md) | — | `symbol_events` | `symbol_events_table` | Symbol configuration change or resync marker |
| Group events | [group_events](PSQL/group_events.md) | — | `group_events` | `group_events_table` | Group configuration change or resync marker |

> **Important:** The *Export Setup* guide's example configuration names the trades table **`trades_export`**; the
> migrator's default is `trades`. A deployment that follows the guide's example migrates with
> `--variables trades_table=trades_export`. Every other default here matches the guide, which does not cover the
> symbol and group pipelines.

### Derived by the migrator

Maintained from an event log by a trigger, never written by the trade server — see
[Event logs and their projections](#event-logs-and-their-projections).

| Table | Page | Derived from | One row per |
|-------|------|--------------|-------------|
| `trading_accounts` | [trading_accounts](PSQL/trading_accounts.md) | `trading_account_events` | Live trading account |
| `clients` | [clients](PSQL/clients.md) | `client_events` | Live client |
| `symbols` | [symbols](PSQL/symbols.md) | `symbol_events` | Live symbol |
| `groups` | [groups](PSQL/groups.md) | `group_events` | Live group |

### Views

| View | Documented on | Over | Answers |
|------|---------------|------|---------|
| `trades_history` | [trades_history](PSQL/trades_history.md) | `trades` | The executed and cancelled trades, without the cash movements |
| `cash_transfers` | [cash_transfers](PSQL/cash_transfers.md) | `trades` | The cash movements, over the columns a transfer actually populates |
| `trading_accounts_resync` | [trading_accounts](PSQL/trading_accounts.md#trading_accounts_resync) | `trading_account_events` | Whether a resync is in progress, per sender |
| `clients_resync` | [clients](PSQL/clients.md#clients_resync) | `client_events` | The same, for the client export |
| `symbols_resync` | [symbols](PSQL/symbols.md#symbols_resync) | `symbol_events` | The same, for the symbol export |
| `groups_resync` | [groups](PSQL/groups.md#groups_resync) | `group_events` | The same, for the group export |

Reading a view needs `SELECT` on the view itself — a grant on the underlying table does not reach through.
`psql/users/ro.sql` grants every view; `exporter` gets none of them, because it writes rows and does not read
reports. The view names are templated (`##trades_history_view##` and so on) even though the trade server never
touches them, so that a database already owning an object under one of the names can move ours aside.

### Journal

| Table | Page |
|-------|------|
| `schema_versions` | [schema_versions](PSQL/schema_versions.md) — renamed with `--journal-table` |

---

## ClickHouse

| Entity | Page | Pipeline `entity` | Default name | Variable | One row per |
|--------|------|-------------------|--------------|----------|-------------|
| Ticks | [ticks](ClickHouse/ticks.md) | `tick` | `ticks` | `ticks_table` | Change of a symbol's best bid or best ask |
| Journal | [schema_versions](ClickHouse/schema_versions.md) | — | `schema_versions` | `--journal-table` | Applied script |

ClickHouse holds nothing else: no cursor table — a ClickHouse pipeline keeps its export position on disk — no views,
and no event logs, since it has no trigger mechanism to maintain a current-state table with.

---

## Conventions

### Every id is scoped to its sender

`sender_id` identifies the trade server instance that produced a row. A deployment may run **several instances**,
each exporting into the same database, and **each numbers its own records**. So every id in this schema —
`trade_id`, `trading_account_id`, `order_id`, `position_id`, `client_id`, `symbol_id`, `group_id` — is unique only
**within a sender**.

> **Warning:** **Never filter or join on an id alone.** Always pair it with `sender_id`. A query that omits it
> silently mixes records belonging to different instances, and it looks correct on a single-instance deployment
> right up until a second instance is added.

```sql
-- WRONG: attributes one instance's trades to another instance's account
FROM trading_accounts a JOIN trades t ON t.trading_account_id = a.trading_account_id

-- RIGHT
FROM trading_accounts a JOIN trades t ON t.sender_id = a.sender_id
                                     AND t.trading_account_id = a.trading_account_id
```

The same applies to counting and grouping: `GROUP BY sender_id, trading_account_id`, never
`GROUP BY trading_account_id`. Every exported and derived table carries `sender_id`, and every unique or primary key
on an exported id leads with it, so the correct query is also the one the keys serve.

### How the exporter writes

Every exported table is **append-only**: only `export_cursor` is ever rewritten, and only the derived tables are
ever deleted from — by their trigger.

On PostgreSQL the exporter writes each batch with `COPY ... FROM STDIN`, naming its columns, in one transaction with
the batch's cursor update. So:

- **Column order is free; names and types are not.** Every column the exporter writes must exist under its name,
  with the type on the entity's page. A mismatch is not detected at start-up — it surfaces later, when a batch is
  rejected and dropped and the trade server logs an `ERROR` (`failed to send batch ... dropping`). Money and price
  columns in particular must be `NUMERIC`; `DOUBLE PRECISION` is rejected.
- **In normal operation a record is written once.** The cursor commits with the data, and on every connection the
  exporter skips past whatever the cursor says is committed. The unique keys are a guard for the case that bypasses
  the cursor — see [Deduplication](#deduplication).

On ClickHouse there is no cursor table and no transaction: after a lost connection the server re-sends the batch it
could not confirm, so delivery there is **at-least-once**, and the table's sorting key absorbs the copies.

### Types

| | PostgreSQL | ClickHouse |
|---|---|---|
| Ids | `BIGINT` | `UInt64` |
| Amounts, prices, rates | `NUMERIC`, unconstrained — stored exactly as sent | `Decimal128(12)` prices, `Decimal64(8)` volumes |
| Timestamps | `TIMESTAMP WITH TIME ZONE` | `DateTime64(6, 'UTC')` |
| Enums | The trade server's ordinal — `SMALLINT` or `INTEGER` — never the name | — |
| Text | `VARCHAR(n)`; `TEXT` for the cursor's table name and the journal | `LowCardinality(String)` |

A column is `NOT NULL` unless its page says otherwise, and the exporter writes a type-appropriate zero (`''`, `0`,
`false`, the epoch) for a field that does not apply rather than `NULL`. The exceptions are deliberate and listed on
their pages: the snapshot columns that can have no value — an unfilled order's fill price, a position without a
stop loss — on the [order](PSQL/daily_statements_order_snapshots.md#columns) and
[position](PSQL/daily_statements_position_snapshots.md#columns) snapshots, and `email_service_id` on the
[group](PSQL/group_events.md#columns) tables. The client tables' two declared amounts are `DOUBLE PRECISION`, the
one place an amount is not `NUMERIC` — see [client_events](PSQL/client_events.md#columns).

> **Warning:** **`NaN` is not `NULL`.** In [`trades`](PSQL/trades.md#nan-not-null) a `NUMERIC` field that does not
> apply to a record may hold the numeric value `NaN`. `IS NULL` never finds it, `= 'NaN'` does, and a single one
> turns `sum()`, `avg()` or `max()` over the column into `NaN`. The snapshot tables write `NULL` instead.

**From the Admin API to a column.** The API's timestamps are microseconds since the Unix epoch; the export writes them
as `TIMESTAMP WITH TIME ZONE`. Each `VARCHAR` holds at least the API's maximum length — usually one more, a field
capped at 63 characters being `VARCHAR(64)`. The API's enumerations are **names**, while the columns store the trade
server's **ordinal**, and the API's declaration order is no guide to the ordinal: its client enums omit the
`Unspecified` member the export numbers `0`, and its transfer types are declared in a different order from
`trade_type`. A page gives an enum's numbers only where the *Export Setup* guide confirms them; elsewhere it lists the
members by name.

### Names

Every table and view name is templated as a `##variable##` and can be renamed with `--variables` — see
[Template Variables](TEMPLATE_VARIABLES.md). The names the trade server writes to mirror its export configuration;
the derived tables and the views are templated so that a database already owning an object under a default name
can move ours aside.

Index, function and trigger names lead with what they cover and end with the table name —
`idx_account_time_trades`, `trg_project_trading_accounts`. PostgreSQL truncates identifiers to 63 bytes, so a long
configured table name would otherwise leave two objects with the same truncated name, and the second
`CREATE … IF NOT EXISTS` would quietly do nothing. For the same reason the migrator refuses a table name too long
for its longest prefix — 36 characters for `trades`, for example; see
[Table names are capped shorter than 63](TEMPLATE_VARIABLES.md#table-names-are-capped-shorter-than-63).

---

## Event logs and their projections

Trading accounts, clients, symbols and groups are exported as an **event log** plus a **current-state table**
derived from it. The mechanism is identical for all four.

```
trade server ──COPY──► <entity>_events ──AFTER INSERT trigger──► <entity> table   (one row per live entity)
                             │
                             └──────────── <entity>_resync view                   (resync in progress?)
```

The log carries four event types, named here as the *Export Setup* guide names them for the trading account and
client logs:

| `event_type` | Name | Meaning | What the trigger does to the current-state table |
|--------------|------|---------|--------------------------------------------------|
| `0` | Upsert | The entity's full state — a creation or a modification | Upserts its row, stamped with the event's `id`. Last write wins, with no version guard |
| `1` | Delete | The entity was removed | Deletes its row |
| `2` | SnapshotStart | A resync begins | **Nothing.** The marker's own `id` is the watermark the end marker reads back |
| `3` | SnapshotEnd | The resync is complete | Deletes that sender's rows stamped older than the newest SnapshotStart before it |
| anything else | — | Unknown to this schema | Nothing — stored in the log, not projected, and no batch fails |

- **Ordering is by arrival** — the log's `id` — never by `version` or `time_inserted`. A whole batch shares one
  `time_inserted`; ids are reused after deletion, which restarts the version sequence; and deletions and markers
  carry `version 0`.
- **A resync sweeps at the end marker**, not the start, so the current-state table stays complete and readable for
  the whole resync — which matters because a resync can follow every restart of the trade server. Between the
  markers the table is a mixture of re-announced and not-yet-reached rows; the entity's `_resync` view says whether
  a sender is in that window.
- **The trigger runs in the exporter's transaction**, so the table cannot lag behind the log, and a batch that fails
  to commit leaves no trace in it. That is also why `exporter` holds `INSERT`, `UPDATE` and `DELETE` on the derived
  tables.
- **The projection script is re-runnable** and ends with a reconcile pass that replays the newest retained event per
  entity, so `--force` repairs a dropped trigger and corrects rows an earlier, buggy version of the projection wrote.
  The pass also re-applies a resync sweep the trigger missed because it was absent when the end marker arrived. No
  reconcile can recover what the retention window has dropped — the authoritative repair for that is a resync from
  the trade server.
- **A resync can be requested on demand** with the Admin REST API: `POST /admin/export/snapshot` with
  `"entityType"` `TradingAccount`, `Client`, `Group` or `Symbol` queues one, and the manager needs the matching
  `initialize…Export` permission. The call returns `202 Accepted` as soon as the request is queued; the
  `_resync` view shows when the resync has started and finished.
- **There is no foreign key** from a derived row to its source event: events age out of retention long before the
  entity does, and a foreign key would make that purge fail or cascade.

Beyond their columns, the four differ only in this:

| Entity | Pages | Verified by | Also differs in |
|--------|-------|-------------|-----------------|
| Trading account | [trading_account_events](PSQL/trading_account_events.md), [trading_accounts](PSQL/trading_accounts.md) | `TradingAccountsProjectionIntegrationTests` | — |
| Client | [client_events](PSQL/client_events.md), [clients](PSQL/clients.md) | `ClientsProjectionIntegrationTests` | Two amounts are `DOUBLE PRECISION`, not `NUMERIC` |
| Symbol | [symbol_events](PSQL/symbol_events.md), [symbols](PSQL/symbols.md) | `SymbolsProjectionIntegrationTests` | No unique key on the symbol name |
| Group | [group_events](PSQL/group_events.md), [groups](PSQL/groups.md) | `GroupsProjectionIntegrationTests` | The one nullable column, `email_service_id`; no unique key on the group name |

The client, symbol and group suites repeat the trading account one's cases and add those their differences call for.
Each test is named for the workflow, edge case or invariant it covers, so a suite reads as a conformance list.

---

## Deduplication

Each exported table has a key that a record cannot be stored twice under, and the engine that holds the table
enforces it in its own way:

| Table | Engine | Key |
|-------|--------|-----|
| [trades](PSQL/trades.md) | PostgreSQL | `(sender_id, trade_id)` |
| [daily_statements_export](PSQL/daily_statements_export.md) | PostgreSQL | `(sender_id, time_statement, trading_account_id)` |
| [daily_statements_order_snapshots](PSQL/daily_statements_order_snapshots.md) | PostgreSQL | `(sender_id, time_statement, order_id)` |
| [daily_statements_position_snapshots](PSQL/daily_statements_position_snapshots.md) | PostgreSQL | `(sender_id, time_statement, position_id)` |
| The event logs | PostgreSQL | **none** — see below |
| [ticks](ClickHouse/ticks.md) | ClickHouse | `(sender_id, short_symbol_name, created_time, sequence)` |

| | PostgreSQL | ClickHouse |
|---|------------|------------|
| Why a record would arrive twice | Only when the cursor is bypassed: a lost local state file makes the pipeline re-export from position `0` | After a lost connection the server re-sends the batch it could not confirm |
| Mechanism | a `UNIQUE` index on the key | `ReplacingMergeTree` sorted by the key |
| When it applies | immediately, on insert | when parts merge — eventually |
| Effect of a duplicate | the **whole batch** is rejected, and the exporter drops it — the new rows in it included | both rows exist until the merge collapses them |
| Reading | always exact | add `FINAL` when duplicates must not be visible |

On PostgreSQL the key is a **guard, not a repair mechanism**. The exporter writes a batch in one transaction, so a
single duplicate fails all of it; the exporter treats that as permanent, logs `failed to send batch ... dropping`
and moves on. A duplicate cannot corrupt the table, but the batch carrying it is lost. In normal operation it never
happens — the cursor keeps the exporter from re-sending what is committed — and the case that reaches the key is
logged as an `ERROR` first. See [`export_cursor`](PSQL/export_cursor.md#what-the-exporter-does-on-connect).

```sql
-- ClickHouse: exact counts before a merge has happened
SELECT count() FROM ticks FINAL;
```

`FINAL` costs more than a plain read. For aggregate reporting over large ranges, consider deduplicating in the query
(`argMax` by the key) or accepting the small window of duplication instead.

The event logs are the exception: an event carries no per-event id, so there is no key to deduplicate on, and an
event that reaches the database twice lands as a second row instead of failing its batch. The derived table absorbs
that — a re-applied change leaves the same state — but a consumer reading a log directly can see one logical change
twice. Adding a per-event id and a unique index on `(sender_id, event_id)` later would be purely additive.

---

## Caveats

### Adding an index to a large, live table

The read-path indexes are added by `psql/20260828_01_add_reporting_indexes.sql`, an ordinary incremental script, so
on an empty or small database they are simply built. On a table already holding a serious number of rows they are
not free: `CREATE INDEX` takes an `ACCESS EXCLUSIVE` lock for the whole build and the exporter's inserts block until
it finishes.

`CREATE INDEX CONCURRENTLY` is the usual escape and **it cannot be used from the migrator** — every PostgreSQL script
runs inside one transaction and PostgreSQL rejects `CONCURRENTLY` inside a transaction block with `25001`. Splitting
the script with `----break----` does not change that; it produces several commands in the same transaction.

The routine is therefore: build them concurrently by hand first, then migrate. Every statement in the script is
`CREATE INDEX IF NOT EXISTS`, so it adopts what is already there and does nothing.

```sql
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_account_time_trades ON trades (trading_account_id, time_created);
```

A cancelled or failed concurrent build leaves an **invalid** index behind, which the planner ignores and
`IF NOT EXISTS` will not replace. Check before trusting it:

```sql
SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid;
```

### `IF NOT EXISTS` everywhere

Every create statement in the scripts is conditional, so the migrator can be pointed at a database that was
provisioned by hand from the guide before this repository existed. It adopts what is already there rather than
failing — which also means it will **not** correct a table whose columns differ from the script. Compare the schema
with the entity's **Final SQL** by hand when adopting a database you did not create. On ClickHouse that includes the
engine: a `ticks` table created by hand without `--cluster` in mind stays a local table when the flag is added
later, and one created as a plain `MergeTree` never deduplicates and fails under `FINAL`.

`IF NOT EXISTS` matches an index by **name**, and the guide's index names differ from the migrator's. On a table the
guide created, the migrator therefore adds its own indexes beside the guide's, and the table carries each one twice:

| Guide's index | Migrator's index, on the same columns |
|---------------|----------------------------------------|
| `idx_trades_trade_id_sender_id` | `idx_sender_trade_<trades_table>` |
| `idx_trades_time_created` | `idx_time_created_<trades_table>` |
| `idx_daily_statements_composite_key` | `idx_sender_stmt_account_<daily_statements_table>` |
| `idx_order_snapshots_composite_key` | `idx_sender_stmt_order_<order_snapshots_table>` |
| `idx_position_snapshots_composite_key` | `idx_sender_stmt_position_<position_snapshots_table>` |

Both of a pair enforce or serve the same thing, so one of them is redundant and only costs writes and space. Drop
the guide's by hand after migrating: the migrator's names are the ones these pages and any later script refer to.

### Renamed tables

If the trade server's export configuration uses non-default table names, every run must pass the same
`--variables trades_table=… daily_statements_table=… order_snapshots_table=… position_snapshots_table=…
trading_account_events_table=… client_events_table=… symbol_events_table=… group_events_table=… cursor_table=…` on
PostgreSQL, or `--variables ticks_table=…` on ClickHouse. Nothing
records the names on the database side, so a run without them falls back to the default names. It does not create a
second set of tables — the create scripts are journalled by file name and never run again. Instead the first
re-runnable script that names a default table fails, and the run with it:

| Engine | Fails in | Left behind |
|--------|----------|-------------|
| PostgreSQL | the first view or users script that names a default table — `views/cash_transfers.sql` for a forgotten `trades_table`, with `relation "trades" does not exist` | nothing: the failing script rolls back in its own transaction, and the run stops there |
| ClickHouse | `post/ticks_ttl.sql`, `UNKNOWN_TABLE` | grants on a `ticks` table that does not exist, `INSERT` for the exporter role and `SELECT` for the read-only one, because the users phase runs first |

Run again with the variables to recover. On ClickHouse that run does not take the stray grants back, so revoke them
by hand:

```sql
REVOKE INSERT ON trading.ticks FROM role_trading_exporter;
REVOKE SELECT ON trading.ticks FROM role_trading_ro;
```

---

## See also

[Writing Migrations](WRITING_MIGRATIONS.md) · [Users & Permissions](USERS_AND_PERMISSIONS.md) ·
[Template Variables](TEMPLATE_VARIABLES.md) · [Architecture](ARCHITECTURE.md)
