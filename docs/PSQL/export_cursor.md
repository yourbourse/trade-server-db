<div align="center">

# export_cursor

**How far each PostgreSQL export pipeline has got — one row per sender and table**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Key and grants](#key-and-grants) · [Scripts](#scripts)

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

The export position of every PostgreSQL pipeline of every trade server instance writing into the database. Create
it once: it is shared by all of them. How the trade server reads and writes it is described in the *Export Setup*
guide.

There is **no cursor on ClickHouse**.

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
| `last_written_position` | `BIGINT` | The export position stored for that sender and table. **Not** a `trade_id` or any other business identifier |

Every column is `NOT NULL`.

---

## Key and grants

The exporter upserts its row with `INSERT ... ON CONFLICT (sender_id, table_name) DO UPDATE`, and `ON CONFLICT`
needs a unique constraint on exactly that pair. **The primary key must be exactly `(sender_id, table_name)`** —
without it every upsert fails.

| The exporter… | Needs |
|---------------|-------|
| reads its row | `SELECT` |
| upserts it | `INSERT`, `UPDATE` |

Among the tables the trade server exports to, `UPDATE` is granted on this one alone. The derived tables get `UPDATE`
and `DELETE` as well, but for their triggers, not for the export — see [Users & Permissions](../USERS_AND_PERMISSIONS.md).

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20260101_02_create_export_cursor.sql`](../../psql/20260101_02_create_export_cursor.sql) | incremental | The table |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

Column descriptions follow the *Export Setup* guide's Export Cursor section.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`trades`](trades.md) · [Users & Permissions](../USERS_AND_PERMISSIONS.md)
