<div align="center">

# trading_account_events

**Every change to a trading account, as an ordered log — the source `trading_accounts` is derived from**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Event types](#event-types) · [Columns](#columns) · [Reading the log](#reading-the-log) · [Indexes](#indexes) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `trading_account_events` — renamed with `--variables trading_account_events_table=…` |
| Written by | The trade server's `trading_account` pipeline — `"entity": "trading_account"`, `"target": "postgresql"` |
| One row per | Event: an account's full state, a deletion, or a resync marker — in order |
| Order | `id` — never `time_inserted`, which a whole batch shares |
| Deduplicated by | Nothing — there is no deduplication key, see [Columns](#columns) |
| Grants | `exporter`: `INSERT`, `SELECT` · `ro`: `SELECT` |
| Derived from it | [`trading_accounts`](trading_accounts.md), by trigger, and the [`trading_accounts_resync`](trading_accounts.md#trading_accounts_resync) view |

When a trading account is created, modified or deleted, the trade server exports an event for the change. The
events form a log from which an account's complete history can be reconstructed — from its creation, through every
modification, to its deletion if any. Reducing the log to "which accounts are live" is the job of
[`trading_accounts`](trading_accounts.md), which a trigger maintains from it in the same transaction — see
[Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections).

---

## Final SQL

The table as the migrator creates it, with the default names substituted. The trigger that projects it into
`trading_accounts` is documented on [that page](trading_accounts.md#projection).

```sql
CREATE TABLE trading_account_events
(
    id                         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted              TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                  BIGINT                   NOT NULL,
    event_type                 SMALLINT                 NOT NULL,
    trading_account_id         BIGINT                   NOT NULL,
    version                    BIGINT                   NOT NULL,
    group_id                   BIGINT                   NOT NULL,
    client_id                  BIGINT                   NOT NULL,
    introducer                 BIGINT                   NOT NULL,
    time_created               TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified              TIMESTAMP WITH TIME ZONE NOT NULL,
    time_password_last_changed TIMESTAMP WITH TIME ZONE NOT NULL,
    leverage                   INTEGER                  NOT NULL,
    enabled                    BOOLEAN                  NOT NULL,
    allow_trading              BOOLEAN                  NOT NULL,
    external_account_id        VARCHAR(64)              NOT NULL DEFAULT '',
    comment                    VARCHAR(256)             NOT NULL DEFAULT ''
);

-- an account's history; used by the projection's reconcile
CREATE INDEX idx_account_row_trading_account_events
    ON trading_account_events (sender_id, trading_account_id, id);

-- the resync markers only, for the watermark lookup at the end of a resync
CREATE INDEX idx_markers_trading_account_events
    ON trading_account_events (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
```

---

## Event types

| Value | Name | Meaning | Content columns | `trading_account_id` |
|-------|------|---------|-----------------|----------------------|
| `0` | Upsert | The account's current full state — used for a creation and a modification alike | All populated | The account |
| `1` | Delete | The account was removed | Zeros | The deleted account |
| `2` | SnapshotStart | A resync begins: every live account follows as an Upsert | Zeros | `0` |
| `3` | SnapshotEnd | The resync is complete | Zeros | `0` |
| anything else | — | Unknown to this schema | — | — |

An unknown event type is stored like any other row and simply not projected, so a trade server that gains one
cannot stall the export of a database that has not been migrated to know it.

---

## Columns

Three service columns and 14 data columns, every one `NOT NULL`.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key, and the write order: ordering by `id` reconstructs the history. The only column that orders events |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row reached PostgreSQL; defaults to `now()`. Transaction start time, so a whole batch shares one — it cannot order events. The column to purge the log by |
| `sender_id` | `BIGINT` | The trade server instance that produced the event, from the pipeline's `sender_id` |
| `event_type` | `SMALLINT` | See [Event types](#event-types) |
| **Identifiers** | | |
| `trading_account_id` | `BIGINT` | API `id`. The account. `0` on both markers |
| `version` | `BIGINT` | The account's version — the API's resource version, incremented on each modification. `0` on a deletion and on both markers |
| `group_id` | `BIGINT` | The account's current group |
| `client_id` | `BIGINT` | The client that owns the account — `client_events.client_id` |
| **Timestamps** | | |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | When the account was created — its own creation time, not when the event was exported |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | When the account's information last changed, as of this event |
| `time_password_last_changed` | `TIMESTAMP WITH TIME ZONE` | When the account's password last changed. The epoch means never |
| **Relationships** | | |
| `introducer` | `BIGINT` | The introducing broker's trading account id. `0` means none — the invalid account id is `0`, never `NULL` |
| **Settings** | | |
| `leverage` | `INTEGER` | The account's leverage, at least `1`; it overrides the group's `default_leverage` |
| `enabled` | `BOOLEAN` | Whether the account is enabled |
| `allow_trading` | `BOOLEAN` | Whether trading is permitted on it |
| `external_account_id` | `VARCHAR(64)` | The account's id in the external trading platform. `''` means none |
| `comment` | `VARCHAR(256)` | Free text |

The data columns are the trade server's trading account as the Admin REST API's `TradingAccount` resource carries it,
less the password; the descriptions combine the API's and the *Export Setup* guide's. The API's timestamps are
microseconds since the Unix epoch; the export writes them as `TIMESTAMP WITH TIME ZONE`.

**The zero convention.** The exporter writes every column on every row. On a Delete and on both markers every
content column carries a type-appropriate zero — `0`, `false`, `''`, the epoch — rather than `NULL`, so a reader has
to check `event_type` to know which columns mean anything. Of the content columns only the two text ones carry a
column default; the rest rely on the exporter. A delete row carries `version 0`, which is why it cannot be ordered
into an account's history by its `version`. Use `id`.

> **Note:** **There is no deduplication key**, unlike every exported table outside the event logs. An event that
> reaches the database twice lands as a second row rather than being rejected. `trading_accounts` absorbs that — a
> re-applied change leaves the same state — but a consumer reading this log directly can see one logical change
> twice.

> **Warning:** The account password is deliberately absent and must stay absent. The Admin API accepts it on a
> write but never returns it, nothing here has a use for it, and leaving it out of the schema is what keeps it out of
> the database — the `ro` role can read every table in here.

---

## Reading the log

For "which accounts are live and how is each configured", read [`trading_accounts`](trading_accounts.md) — it is
this log already reduced. Read the log itself for an account's history, or to build a projection of your own.

A **resync** is a bracketed range: a SnapshotStart, then an Upsert for every live account, then a SnapshotEnd. It is
a full re-export of the accounts' state at that moment — the *Export Setup* guide names a server restart or an
interrupted connection as occasions for one, and an administrator can request one at any time with the Admin API's
`POST /admin/export/snapshot`, `"entityType": "TradingAccount"`, which needs the manager permission
`initializeTradingAccountExport`. A consumer starting fresh treats the Upserts between a SnapshotStart and its
SnapshotEnd as a consistent snapshot, and ignores what came before the SnapshotStart in its favour. An account the
resync did not re-announce no longer exists.

```sql
-- one account's history, in the order it was recorded
SELECT id, time_inserted, event_type, version, group_id, leverage, enabled, allow_trading
FROM trading_account_events
WHERE sender_id = 1
  AND trading_account_id = 100042
ORDER BY id;
```

---

## Indexes

| Index | Definition | Purpose |
|-------|------------|---------|
| `trading_account_events_pkey` | `PRIMARY KEY (id)` | |
| `idx_account_row_trading_account_events` | `(sender_id, trading_account_id, id)` | An account's history in recorded order; the projection's reconcile pass |
| `idx_markers_trading_account_events` | `(sender_id, event_type, id) WHERE event_type IN (2, 3)` | The resync markers only, for the watermark the end marker looks up |

The *Export Setup* guide creates the table without either index; on a table provisioned by hand from the guide the
migrator adds both.

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20260908_01_create_trading_account_events.sql`](../../psql/20260908_01_create_trading_account_events.sql) | incremental | The table and both indexes |
| [`psql/views/trading_accounts_projection.sql`](../../psql/views/trading_accounts_projection.sql) | views | The `AFTER INSERT` trigger on this table — see [`trading_accounts`](trading_accounts.md#projection) |
| [`psql/views/trading_accounts_resync.sql`](../../psql/views/trading_accounts_resync.sql) | views | The resync view over the markers |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

Column descriptions and the event type names follow the *Export Setup* guide's Trading Account Events section and
the Admin REST API's `TradingAccount`. The
design behind the log and its projection — workflows, edge cases, invariants and the decisions taken — is in the
[trading accounts specification](../specs/trading-accounts/SPEC.md) and its [design](../specs/trading-accounts/design.md).

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`trading_accounts`](trading_accounts.md) ·
[Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections)
