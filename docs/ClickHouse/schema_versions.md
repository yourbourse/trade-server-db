<div align="center">

# schema_versions (ClickHouse)

**The migrator's journal — which scripts have been applied to this database**

ClickHouse · written by the migrator · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Entries](#entries) · [PostgreSQL](../PSQL/schema_versions.md)

</div>

---

## Overview

| | |
|---|---|
| Engine | ClickHouse. PostgreSQL keeps the same journal — [its page](../PSQL/schema_versions.md) |
| Default name | `schema_versions` — renamed with `--journal-table …` |
| Written by | The migrator, never the trade server |
| Created by | The migrator's journal ([`ClickHouseTableJournal`](../../src/DbUp.ClickHouse/ClickHouseTableJournal.cs)), in the connection string's database — no script under `clickhouse/` touches it |
| One row per | Applied script — one per re-runnable script, and one per account for a users script |
| Key | `PRIMARY KEY (script_name)`, `ORDER BY (script_name, applied_at)` — not unique; deleting the previous entry before each insert keeps one row per script |
| Engine clause | `MergeTree`, or `ReplicatedMergeTree` with `--cluster` |
| Grants | None for `exporter` or `ro` — only the account that runs migrations reads it |

The journal is the migrator's own bookkeeping, created on the first run in the database named by the connection
string. Its shape is identical on both engines by design.

---

## Final SQL

The table as the migrator creates it on its first run on a single instance, under the default name. With
`--cluster` it is created `ON CLUSTER` as a `ReplicatedMergeTree`.

```sql
CREATE TABLE schema_versions
(
    script_name LowCardinality(String),
    applied_at  DateTime64(6, 'UTC')
)
ENGINE = MergeTree
PRIMARY KEY (script_name)
ORDER BY (script_name, applied_at);
```

---

## Columns

| Column | Type | Notes |
|--------|------|-------|
| `script_name` | `LowCardinality(String)` | The journal entry — see [Entries](#entries) |
| `applied_at` | `DateTime64(6, 'UTC')` | When the script was applied |

---

## Entries

| Kind | Journal entry | Re-runs when |
|------|---------------|--------------|
| Incremental | `[clickhouse] 20260922_01_create_ticks.sql` | never |
| Re-runnable (`pre/`, `views/`, `post/`) | `[clickhouse/post] ticks_ttl.sql::a1b2…` | its processed content changes — a new `ticks_ttl_days`, `position_events_delete_ttl_days`, `position_events_min_age_to_force_merge_seconds` or `positions_refresh_seconds` included |
| Users | `[clickhouse/users] exporter.sql for trade_exporter::7f3c…` | its content, name or password changes |

The `::` suffix is the MD5 of the processed script. Before writing a new entry the migrator deletes every previous
entry for the same script, so one row per re-runnable script — per account, for a users script — survives.
`--force` re-applies the re-runnable phases without reading or writing the journal — see
[Journalling](../ARCHITECTURE.md#journalling).

```sql
SELECT script_name, applied_at FROM schema_versions ORDER BY applied_at;
```

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [ClickHouse Provider](../CLICKHOUSE_PROVIDER.md) ·
[Writing Migrations](../WRITING_MIGRATIONS.md)
