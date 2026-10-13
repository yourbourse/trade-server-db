<div align="center">

# export_cursor

**How far each PostgreSQL export pipeline has got — one row per sender and table**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [How the cursor is used](#how-the-cursor-is-used) · [On connect](#what-the-exporter-does-on-connect) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `export_cursor` — renamed with `--variables cursor_table=…` |
| Written by | Every PostgreSQL pipeline of the trade server — the table each one names in its `cursor_table` field, the same in every pipeline |
| One row per | `(sender_id, table_name)` — a server instance and the table one of its pipelines exports into |
| Key | `PRIMARY KEY (sender_id, table_name)` — required exactly as it is, see below |
| Grants | `exporter`: `INSERT`, `SELECT`, `UPDATE` · `ro`: `SELECT` |

The export position of every PostgreSQL pipeline of every trade server instance writing into the database, so that
export resumes where it stopped after an interruption. Create it once: it is shared by all of them.

It is the **authoritative** export position. The trade server's local `state.bin` is only a cache saved on a graceful
shutdown; on every connection the pipeline reconciles against the row here.

There is **no cursor on ClickHouse**. A ClickHouse pipeline keeps its position on disk only, and after a crash
starts again from position `0`.

---

## Final SQL

The table as the migrator creates it, under the default name.

```sql
CREATE TABLE export_cursor
(
    sender_id             BIGINT NOT NULL,
    table_name            TEXT   NOT NULL,
    last_written_position BIGINT NOT NULL,
    PRIMARY KEY (sender_id, table_name)
);
```

---

## Columns

| Column | Type | Description |
|--------|------|-------------|
| `sender_id` | `BIGINT` | The trade server instance, from the pipeline's `sender_id`. Each instance writing into the database must use its own value. Part of the primary key |
| `table_name` | `TEXT` | The exported table, exactly as given in the pipeline's `table` field — `trades`, `daily_statements_export`, … Part of the primary key |
| `last_written_position` | `BIGINT` | How many records the pipeline has committed to that table — the position the next record takes. Internal to the export buffer: it counts the records through that one pipeline from `0`, is **not** a `trade_id` or any other business identifier, and restarts at `0` whenever the pipeline's data folder is cleared |

Every column is `NOT NULL`.

---

## How the cursor is used

The cursor row and the batch of records it covers are written **in the same transaction**: if the transaction fails,
neither changes, so the cursor can never claim more than was actually stored. That is what makes it trustworthy
enough to resume from.

The exporter upserts its row with `INSERT ... ON CONFLICT (sender_id, table_name) DO UPDATE` on every batch, and
`ON CONFLICT` needs a unique constraint on exactly that pair. **The primary key must be exactly
`(sender_id, table_name)`** — without it every batch fails.

| The exporter… | Needs |
|---------------|-------|
| reads its position on every connection | `SELECT` |
| upserts it with every batch | `INSERT`, `UPDATE` |

Among the tables the trade server exports to, `UPDATE` is granted on this one alone. It is the only row the export
ever rewrites — an exported record is never updated or deleted. The derived tables get `UPDATE` and `DELETE` as well,
but for their triggers, not for the export — see [Users & Permissions](../USERS_AND_PERMISSIONS.md).

---

## What the exporter does on connect

On start-up and after every reconnection, a PostgreSQL pipeline compares its local position with
`last_written_position`. A database outage long enough to drop the connection therefore produces one of these when it
ends, not only a restart.

| Situation | What happens | Logged |
|-----------|--------------|--------|
| The positions match | Normal operation resumes | — |
| The database is ahead — the server crashed after a batch committed but before it saved its local state | The already-committed records are skipped, so nothing is written twice | `INFO` `skipping already committed records ... position range [X, Y)` |
| The database is behind — records were lost from the local buffer, for example in a crash | The missing range is lost for good; the pipeline logs the gap and continues. A manual backfill may be needed | `ERROR` `data gap detected ... position range [X, Y) missing` |
| The local position is `0` but the database holds records — the local state was lost or the data folder cleared | The pipeline cannot tell what the database already holds, so it re-exports from `0` rather than risk skipping any | `ERROR` `persistent state not loaded ... starting from cursor 0` |

The last case is the one that reaches the data tables' unique keys. Every batch that re-sends a row the database
already holds is rejected whole and **dropped**, any new records in it included, with `failed to send batch ...
dropping` logged until the re-sent range is past. Treat the overlapping range as needing verification rather than
assuming the unique index cleaned it up — see [`trades`](trades.md#indexes). The event logs have no unique key, so
there the re-sent events are stored a second time instead, and the derived tables absorb them.

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20260101_02_create_export_cursor.sql`](../../psql/20260101_02_create_export_cursor.sql) | incremental | The table |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

Column descriptions and the connect-time behaviour follow the *Export Setup* guide's Export Cursor and How the
Cursor Works sections.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`trades`](trades.md) · [Users & Permissions](../USERS_AND_PERMISSIONS.md)
