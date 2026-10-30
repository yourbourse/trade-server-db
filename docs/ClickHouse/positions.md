<div align="center">

# positions

**The open positions of every sender, one row each, stored — a refreshable materialized view over `positions_live`**

ClickHouse · view · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Refresh](#refresh) · [Reading it](#reading-it) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | ClickHouse, 24.10 or later |
| Kind | Refreshable materialized view over [`positions_live`](positions_live.md), into a plain `MergeTree` |
| Default name | `positions` — renamed with `--variables positions_view=…` |
| One row per | Open position of every sender, as of the latest complete snapshot and the changes after it |
| Columns | Those of `positions_live`: every column of `position_events` except `marker_sequence` |
| Refresh | Every 30 seconds by default — `--variables positions_refresh_seconds=…` |
| Grants | `ro`: `SELECT` · `exporter`: none |

[`positions_live`](positions_live.md) computes the current state of the positions from the
[`position_events`](position_events.md) change stream on every read. This view stores that result: on every refresh it runs
`SELECT * FROM positions_live` and replaces its rows with what comes back. Readers get a plain table and never
pay for `FINAL` themselves; the price is staleness — the view shows the stream as it stood at the last refresh.

What the rows are — the watermark, the snapshot rules and a worked example — is on
[`positions_live`](positions_live.md#the-snapshot-protocol), which holds the one definition of the state.
Which of the two to read is compared under [Which one to read](positions_live.md#which-one-to-read).

The trade server never reads or writes it. The view name is templated so that a database already owning an object
called `positions` can move ours aside.

---

## Final SQL

The view as it stands once the script that builds it has run, on a single instance with the default names and the
default 30-second refresh.

```sql
CREATE MATERIALIZED VIEW positions
REFRESH EVERY 30 SECOND
ENGINE = MergeTree
ORDER BY (sender_id, position_id)
AS
SELECT *
FROM positions_live;
```

With `--cluster my_cluster` the statements run `ON CLUSTER 'my_cluster'`; the target stays a plain `MergeTree`. Every
node refreshes the view from its own replica of `position_events`, so there is nothing to replicate, and a replicated
target would have the replicas overwrite each other's refreshes.

`SHOW CREATE TABLE` prints the source database-qualified, `FROM trading.positions_live`, and adds
`DEFINER = <account> SQL SECURITY DEFINER` naming the account that ran the migration — see [Refresh](#refresh). It
prints the period in the largest whole unit: `REFRESH EVERY 3600 SECOND` reads back as `REFRESH EVERY 1 HOUR`.

---

## Columns

Every column of [`positions_live`](positions_live.md#columns), which are those of
[`position_events`](position_events.md#columns) except `marker_sequence`, with the same types and meanings and in the same
order. `event_type` is always `Upsert` and `position_id` is never `0`.

---

## Refresh

The view is refreshed every `positions_refresh_seconds` seconds, 30 by default. A refresh recomputes the
whole result through `positions_live` and replaces the view's rows with it in one step, so a reader sees
either the previous state or the new one, never a mix. A deployment sets its own period with
`--variables positions_refresh_seconds=N`; `N` must be a positive integer.

`post/positions.sql` does both jobs. It creates the view with `CREATE MATERIALIZED VIEW IF NOT EXISTS` and the
configured period, then runs `ALTER TABLE … MODIFY REFRESH EVERY N SECOND`. A changed value changes the script's
processed content, so it re-runs: `IF NOT EXISTS` keeps the existing view and its rows, and `MODIFY REFRESH` changes
the schedule only. With the default the `ALTER` changes nothing.

> **Note:** The view is created in `post/` because it selects from `positions_live`, which the `views/` phase
> creates after the incremental scripts. Its query is a fixed `SELECT *` and never needs replacing: a change to the
> state is a change to `positions_live`, which `CREATE OR REPLACE VIEW` applies on the next run and this view
> picks up on its next refresh.

The refresh runs as the account that ran the migration: ClickHouse records it as the view's definer. Dropping that
account, or taking its `SELECT` on `positions_live` or `position_events` away, makes every later refresh fail and
leaves the rows of the last successful one in place. `positions_live` runs as its invoker, which during a
refresh is that definer.

Refreshable materialized views left experimental in ClickHouse 24.10, the oldest release this script runs on. On an
older server the script fails, and the run with it.

A refresh reads the whole of `position_events` with `FINAL`. Its cost grows with the open positions and with the snapshot
markers, which are never removed, rather than with the history of the positions: the merges collapse old versions
and the TTL removes old `Delete` rows — see [position_events → Retention](position_events.md#retention).

---

## Reading it

- Read it as a plain table: no `FINAL`, no filter on `event_type` or `position_id`. Every row is an open position.
- The data is as fresh as the last refresh — up to the refresh period, plus the time a refresh takes, behind
  `position_events`. Read [`positions_live`](positions_live.md) when that is too old.
- Filter on `sender_id`, and pair every id with it — see
  [Every id is scoped to its sender](../SCHEMA_REFERENCE.md#every-id-is-scoped-to-its-sender). A sender with no
  complete snapshot yet has no rows.
- With `--cluster`, each node holds its own copy, refreshed from its own replica of `position_events`; two nodes can be a
  refresh apart.
- `system.view_refreshes` shows the state of the refresh, its last success and the next run. A failing refresh
  leaves the previous rows in place.

```sql
-- the open positions of one account
SELECT position_id, symbol_name, side, volume, avg_open_price, pl
FROM positions
WHERE sender_id = 1
  AND trading_account_id = 1001
ORDER BY position_id;

-- open volume per symbol and side
SELECT symbol_name, side, sum(volume) AS volume, count() AS position_events
FROM positions
WHERE sender_id = 1
GROUP BY symbol_name, side
ORDER BY symbol_name, side;

-- how the refresh is doing
SELECT *
FROM system.view_refreshes
WHERE database = 'trading' AND view = 'positions';
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`clickhouse/post/positions.sql`](../../clickhouse/post/positions.sql) | post | The view, if missing, then `MODIFY REFRESH EVERY`. Re-runs when `positions_refresh_seconds` changes |
| [`clickhouse/users/ro.sql`](../../clickhouse/users/ro.sql) | users | `SELECT` for `role_<database>_ro` — see [Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse) |

The users phase runs before `post/`, so on a first run the grant is applied before the view exists; ClickHouse
accepts a grant on an object that does not exist yet.

The exporter is granted nothing on the view: it only inserts into `position_events`, and the refresh is run by the server.
The suites in `Execution/Positions/` cover the view alongside the table — see
[position_events → Scripts](position_events.md#scripts).

---

## See also

[`positions_live`](positions_live.md) · [`position_events`](position_events.md) ·
[Schema Reference](../SCHEMA_REFERENCE.md) · [Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse)
