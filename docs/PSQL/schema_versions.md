<div align="center">

# schema_versions (PostgreSQL)

**The migrator's journal — which scripts have been applied to this database**

PostgreSQL · written by the migrator · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Entries](#entries) · [ClickHouse](../ClickHouse/schema_versions.md)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL. ClickHouse keeps the same journal — [its page](../ClickHouse/schema_versions.md) |
| Default name | `schema_versions` — renamed with `--journal-table …` |
| Written by | The migrator, never the trade server |
| Created by | The migrator's journal ([`PostgresTableJournal`](../../src/DbUp.PSQL/PostgresTableJournal.cs)), in the `public` schema — no script under `psql/` touches it |
| One row per | Applied script — one per re-runnable script, and one per account for a users script |
| Key | `PRIMARY KEY (script_name)` |
| Grants | None for `exporter` or `ro` — only the account that runs migrations reads it |

The journal is the migrator's own bookkeeping, created on the first run in the database named by the connection
string. Its shape is identical on both engines by design.

---

## Final SQL

The table as the migrator creates it on its first run, under the default name.

```sql
CREATE TABLE schema_versions
(
    script_name TEXT                     NOT NULL,
    applied_at  TIMESTAMP WITH TIME ZONE NOT NULL,
    CONSTRAINT "PK_schema_versions_Id" PRIMARY KEY (script_name)
);
```

---

## Columns

| Column | Type | Notes |
|--------|------|-------|
| `script_name` | `TEXT` | Primary key. The journal entry — see [Entries](#entries) |
| `applied_at` | `TIMESTAMP WITH TIME ZONE` | When the script was applied |

---

## Entries

| Kind | Journal entry | Re-runs when |
|------|---------------|--------------|
| Incremental | `[psql] 20260101_01_create_trades.sql` | never |
| Re-runnable (`pre/`, `views/`, `post/`) | `[psql/post] run_on_every_build.sql::a1b2…` | its processed content changes |
| Users | `[psql/users] exporter.sql for trade_exporter::7f3c…` | its content, name or password changes |

The `::` suffix is the MD5 of the processed script. Before writing a new entry the migrator deletes every previous
entry for the same script, so one row per re-runnable script — per account, for a users script — survives.
`--force` re-applies the re-runnable phases without reading or writing the journal — see
[Journalling](../ARCHITECTURE.md#journalling).

```sql
SELECT script_name, applied_at FROM schema_versions ORDER BY applied_at;
```

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [Writing Migrations](../WRITING_MIGRATIONS.md) ·
[Architecture](../ARCHITECTURE.md)
