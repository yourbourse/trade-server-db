<div align="center">

# trading_accounts

**The current state of every live trading account — derived from `trading_account_events` by a trigger**

PostgreSQL · maintained by the migrator's trigger, never by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Projection](#projection) · [trading_accounts_resync](#trading_accounts_resync) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `trading_accounts` — renamed with `--variables trading_accounts_table=…` |
| Written by | The trigger `trg_project_trading_accounts` on [`trading_account_events`](trading_account_events.md), in the same transaction as the event |
| One row per | Live trading account, keyed by `(sender_id, trading_account_id)` |
| Key | `PRIMARY KEY (sender_id, trading_account_id)` |
| Grants | `exporter`: `INSERT`, `SELECT`, `UPDATE`, `DELETE` — the trigger runs as the exporter · `ro`: `SELECT` |
| Companion view | [`trading_accounts_resync`](#trading_accounts_resync) — whether a resync is in progress, per sender |

"Which accounts are live and how is each configured" is a single read here. The table is **not written by the
trade server**: it is derived from the event log by a trigger inside the same transaction as the event that caused
the change, so it cannot lag behind the log, and a batch that fails to commit leaves no trace in it.

The name is templated even though the trade server never writes to it, so that a database already owning an
object called `trading_accounts` can move ours aside.

---

## Final SQL

The table and its companion view as the migrator creates them, with the default names substituted. The projection
function and trigger are under [Projection](#projection).

```sql
CREATE TABLE trading_accounts
(
    sender_id                  BIGINT                   NOT NULL,
    trading_account_id         BIGINT                   NOT NULL,
    version                    BIGINT                   NOT NULL,
    group_id                   BIGINT                   NOT NULL,
    client_id                  BIGINT                   NOT NULL,
    leverage                   INTEGER                  NOT NULL,
    introducer                 BIGINT                   NOT NULL,
    external_account_id        VARCHAR(64)              NOT NULL,
    comment                    VARCHAR(256)             NOT NULL,
    enabled                    BOOLEAN                  NOT NULL,
    allow_trading              BOOLEAN                  NOT NULL,
    time_created               TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified              TIMESTAMP WITH TIME ZONE NOT NULL,
    time_password_last_changed TIMESTAMP WITH TIME ZONE NOT NULL,
    -- the event row this state came from
    source_event_row_id        BIGINT                   NOT NULL,
    -- when the projection last wrote the row
    time_applied               TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),

    PRIMARY KEY (sender_id, trading_account_id)
);

CREATE VIEW trading_accounts_resync AS
SELECT sender_id,
       max(id) FILTER (WHERE event_type = 2)            AS resync_started_event_row_id,
       max(time_inserted) FILTER (WHERE event_type = 2) AS time_resync_started,
       max(time_inserted) FILTER (WHERE event_type = 3) AS time_resync_finished,
       COALESCE(max(id) FILTER (WHERE event_type = 2), -1) >
       COALESCE(max(id) FILTER (WHERE event_type = 3), -1) AS resync_in_progress
FROM trading_account_events
WHERE event_type IN (2, 3)
GROUP BY sender_id;
```

---

## Columns

Every account column of [`trading_account_events`](trading_account_events.md#columns), with the same types and the
same meanings, minus the log's own envelope (`id`, `time_inserted`, `event_type`) and plus:

| Column | Type | Notes |
|--------|------|-------|
| `sender_id`, `trading_account_id` | `BIGINT` | `PRIMARY KEY (sender_id, trading_account_id)` |
| `source_event_row_id` | `BIGINT` | The `trading_account_events.id` this state came from. Traceability, and what a resync's end marker compares against |
| `time_applied` | `TIMESTAMP WITH TIME ZONE` | When the projection last wrote the row, as against `time_modified`, when the account last changed |

Every column is `NOT NULL`. There is deliberately **no foreign key** on `source_event_row_id`: an event ages out of
the retention window long before the account does, and a foreign key would make that purge either fail or cascade
into here — which is the one thing this table exists not to do.

No indexes beyond the primary key. The table is bounded at roughly the live account count, so a filter the primary
key cannot serve is a scan of a small table rather than of a growing one.

---

## Projection

[`psql/views/trading_accounts_projection.sql`](../../psql/views/trading_accounts_projection.sql) creates the function
`project_trading_accounts()` and this trigger on the log:

```sql
CREATE TRIGGER trg_project_trading_accounts
    AFTER INSERT
    ON trading_account_events
    FOR EACH ROW
EXECUTE FUNCTION project_trading_accounts();
```

| `event_type` | What the trigger does |
|--------------|-----------------------|
| `0` Upsert | Upserts the account's row, stamped with the event's `id`. Last write wins, with no version guard |
| `1` Delete | Deletes the account's row |
| `2` SnapshotStart | **Nothing.** The row's own `id` is the watermark the end marker reads back out of the log |
| `3` SnapshotEnd | Deletes that sender's rows stamped older than the newest SnapshotStart before it |
| anything else | Nothing — stored in the log, not projected, and no batch fails |

Ordering is by arrival, never by `version`: trading account ids are reused after deletion, so a reused id restarts
its version sequence, and deletions and markers carry `version 0` anyway.

A resync sweeps at the **end** marker rather than the start, so the table stays complete and readable for the
whole resync — which matters because a resync can follow every restart of the trade server.

The script is re-runnable and ends with a reconcile pass that replays the newest retained event per account, so
`--force` repairs a dropped trigger and corrects rows an earlier, buggy version of the projection wrote. The pass also
re-applies a resync sweep the trigger missed because it was absent when the end marker arrived. It cannot recover
what the retention window has dropped; the authoritative repair for that is a resync from the trade server,
which an administrator can request with the Admin API's `POST /admin/export/snapshot`,
`"entityType": "TradingAccount"`. The shared mechanism is described once in
[Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections).

---

## trading_accounts_resync

Whether each sender's picture of the live account set is settled or still converging, aggregated from the resync
markers in [`trading_account_events`](trading_account_events.md#event-types) — `event_type` `2` SnapshotStart and
`3` SnapshotEnd. There is no
table behind it: the markers are the record of every resync, so the answer cannot disagree with the log. A sender
that has never resynced has no row.

| Column | Type | Notes |
|--------|------|-------|
| `sender_id` | `BIGINT` | One row per sender that has ever resynced |
| `resync_started_event_row_id` | `BIGINT` | The log row the newest start marker arrived as — the watermark the end marker compares `source_event_row_id` against |
| `time_resync_started` | `TIMESTAMP WITH TIME ZONE` | When the newest resync started |
| `time_resync_finished` | `TIMESTAMP WITH TIME ZONE` | When a resync last finished. While one is in progress this belongs to the *previous* resync — read it together with `resync_in_progress` |
| `resync_in_progress` | `BOOLEAN` | A start marker with no end marker after it |

It matters because `trading_accounts` carries a mixture between the two markers: accounts the resync has
re-announced are current, accounts it has not reached yet still carry their pre-resync state, and accounts deleted
while the sender was down survive until the end marker sweeps them. Bounded and plausible rather than wrong — and a
reader that cares can tell the two situations apart here.

| | |
|---|---|
| Default name | `trading_accounts_resync` — renamed with `--variables trading_accounts_resync_view=…` |
| Grants | `ro`: `SELECT` · `exporter`: none |

```sql
-- the live accounts, and whether the sender's picture is still converging
SELECT a.sender_id, a.trading_account_id, a.group_id, a.leverage, a.enabled,
       COALESCE(r.resync_in_progress, false) AS resync_in_progress
FROM trading_accounts a
         LEFT JOIN trading_accounts_resync r ON r.sender_id = a.sender_id;
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20260908_02_create_trading_accounts.sql`](../../psql/20260908_02_create_trading_accounts.sql) | incremental | The table |
| [`psql/views/trading_accounts_projection.sql`](../../psql/views/trading_accounts_projection.sql) | views | `project_trading_accounts()`, `trg_project_trading_accounts` and the reconcile pass |
| [`psql/views/trading_accounts_resync.sql`](../../psql/views/trading_accounts_resync.sql) | views | The `trading_accounts_resync` view |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

The specification the projection is tested against is [trading accounts — SPEC](../specs/trading-accounts/SPEC.md);
`TradingAccountsProjectionIntegrationTests` names each test for the clause it covers.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`trading_account_events`](trading_account_events.md) ·
[Trading accounts — design](../specs/trading-accounts/design.md)
