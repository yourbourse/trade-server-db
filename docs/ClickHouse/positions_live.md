<div align="center">

# positions_live

**The open positions of every sender, one row each, computed on every read — a view over `position_events`**

ClickHouse · view · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [The snapshot protocol](#the-snapshot-protocol) · [Which one to read](#which-one-to-read) · [Reading it](#reading-it) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | ClickHouse |
| Kind | Plain view over [`position_events`](position_events.md), `SQL SECURITY INVOKER` |
| Default name | `positions_live` — renamed with `--variables positions_live_view=…` |
| One row per | Open position of every sender, as of the latest complete snapshot and the changes after it |
| Columns | Every column of `position_events` except the `MATERIALIZED` `marker_sequence` |
| Freshness | Always current: the query runs on every read |
| Grants | `ro`: `SELECT`, which also needs `SELECT` on `position_events` · `exporter`: none |

[`position_events`](position_events.md) is a change stream: every version of every position until the merges collapse them,
the `Delete` rows of closed positions, and the markers of each snapshot. This view turns it into the current state.
It reads the table with `FINAL`, keeps the latest version of each position, drops the closed ones and the markers,
and hides every position the latest complete snapshot no longer holds.

This is **the one definition of the state**. [`positions`](positions.md) stores the same rows on a
schedule by selecting from this view, so a change to the query here reaches it on its next refresh.

The trade server never reads or writes it. The view name is templated so that a database already owning an object
called `positions_live` can move ours aside.

---

## Final SQL

The view as the migrator creates it, under the default names.

```sql
CREATE OR REPLACE VIEW positions_live
AS
WITH
    snapshot_ends AS
    (
        SELECT sender_id, max(sequence) AS end_sequence
        FROM position_events
        WHERE position_id = 0 AND event_type = 'SnapshotEnd'
        GROUP BY sender_id
    ),
    watermarks AS
    (
        SELECT starts.sender_id, max(starts.sequence) AS watermark
        FROM position_events AS starts
        INNER JOIN snapshot_ends ON starts.sender_id = snapshot_ends.sender_id
        WHERE starts.position_id = 0
          AND starts.event_type = 'SnapshotStart'
          AND starts.sequence < snapshot_ends.end_sequence
        GROUP BY starts.sender_id
    )
SELECT position_events.*
FROM position_events AS position_events FINAL
INNER JOIN watermarks ON position_events.sender_id = watermarks.sender_id
WHERE position_events.position_id != 0
  AND position_events.event_type = 'Upsert'
  AND position_events.sequence >= watermarks.watermark
SETTINGS do_not_merge_across_partitions_select_final = 1;
```

With `--cluster my_cluster` the statement runs `ON CLUSTER 'my_cluster'`, and every node's view reads its own
replica of `position_events`.

The view runs with the rights of whoever reads it — `SQL SECURITY INVOKER`, the default for a plain view, which
`SHOW CREATE TABLE` does not print. So a reader needs `SELECT` on `position_events` as well as on the view.

`do_not_merge_across_partitions_select_final = 1` lets `FINAL` work one partition at a time. `position_events` is
partitioned by `sender_id`, which is the start of its sorting key, so no two versions of one position are ever in
different partitions and the result is the same.

---

## Columns

Every column of [`position_events`](position_events.md#columns), with the same types and meanings, in the same order, except
`marker_sequence` — a `MATERIALIZED` column, which `SELECT position_events.*` does not return, and which is `0` on every
row the view keeps.

`event_type` is always `Upsert` and `position_id` is never `0`. `sequence` and `batch_time` are those of the
position's latest change.

---

## The snapshot protocol

The server sends a position as an `Upsert` whenever it changes and as a `Delete` when it closes. From time to time
it also sends a full snapshot: a `SnapshotStart`, an `Upsert` for every position it holds open, and a `SnapshotEnd`.
The snapshot is what lets the view forget a position whose `Delete` never arrived: a position the server no longer
knows is simply not in it.

For every sender the view computes a **watermark**: the `sequence` of the latest `SnapshotStart` that has a
`SnapshotEnd` after it. Then:

| Rule | Effect |
|------|--------|
| A position whose latest row is older than the watermark is hidden | It was not part of the latest complete snapshot, so the server no longer holds it |
| A position whose latest row is a `Delete` is hidden | It was closed |
| A sender with no complete snapshot has no watermark | The sender is absent from the view until its first snapshot completes |
| A snapshot still in progress does not move the watermark | Until its `SnapshotEnd` arrives the previous watermark stands, so a half-sent snapshot hides nothing |

The `Upsert`s of a snapshot in progress are changes like any other: one after the current watermark replaces the
position's earlier version at once.

### Worked example

One sender, `sender_id = 1`. Each line is a row of `position_events`; the right-hand column is what the view returns once
that row has arrived — and what [`positions`](positions.md) holds after its next refresh.

| `sequence` | `event_type` | `position_id` | Watermark | View returns |
|-----------:|--------------|--------------:|----------:|--------------|
| 1 | `SnapshotStart` | 0 | — | nothing: no complete snapshot, sender 1 absent |
| 2 | `Upsert` | 10 | — | nothing |
| 3 | `Upsert` | 11 | — | nothing |
| 4 | `SnapshotEnd` | 0 | 1 | 10 (seq 2), 11 (seq 3) |
| 5 | `Upsert` | 11 | 1 | 10 (seq 2), 11 (seq 5) |
| 6 | `Upsert` | 12 | 1 | 10, 11, 12 (seq 6) |
| 7 | `Delete` | 10 | 1 | 11, 12 — 10 is closed |
| 8 | `SnapshotStart` | 0 | 1 | 11, 12 — the new snapshot is in progress and changes nothing |
| 9 | `Upsert` | 11 | 1 | 11 (seq 9), 12 (seq 6) |
| 10 | `SnapshotEnd` | 0 | 8 | 11 (seq 9) — 12 was not in the snapshot, its row 6 is older than the watermark |

Position 12 disappears without a `Delete`: the snapshot from 8 to 10 did not contain it, so the server no longer
holds it. Had the server sent another `SnapshotStart` at 11 and nothing more, the watermark would stay at 8 and the
view would not change.

The rules rely on `sequence` growing monotonically per sender: the watermark is a comparison of sequences.

---

## Which one to read

The two views return the same rows; they differ in when the work is done.

| | `positions_live` | [`positions`](positions.md) |
|---|------------------------|------------------------------------------|
| Kind | Plain view | Refreshable materialized view over this one |
| Freshness | Current at every read | As old as the last refresh — up to `positions_refresh_seconds`, 30 by default, plus the time a refresh takes |
| Cost per read | A `FINAL` read of the whole of `position_events` | A read of a plain `MergeTree` table |
| Grants a reader needs | `SELECT` on the view and on `position_events` | `SELECT` on the view |
| Server version | any the migrator supports | 24.10 or later |

Read this view when a change must be visible as soon as it lands — a support check on one account, a reconciliation
against the server. Read `positions` for dashboards, reports and anything that queries often.

---

## Reading it

- Read it as a plain table: no `FINAL`, no filter on `event_type` or `position_id`. Every row is an open position.
- Every read pays for the whole query — `FINAL` over `position_events`, and the watermark join. A filter on `sender_id`
  narrows what the outer read returns; do not count on it to make the query cheaper.
- Filter on `sender_id`, and pair every id with it — see
  [Every id is scoped to its sender](../SCHEMA_REFERENCE.md#every-id-is-scoped-to-its-sender). A sender with no
  complete snapshot yet has no rows.

```sql
-- the open positions of one account, as they stand now
SELECT position_id, symbol_name, side, volume, avg_open_price, pl
FROM positions_live
WHERE sender_id = 1
  AND trading_account_id = 1001
ORDER BY position_id;
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`clickhouse/views/positions_live.sql`](../../clickhouse/views/positions_live.sql) | views | The view, as `CREATE OR REPLACE VIEW`. Re-runs when its content changes |
| [`clickhouse/users/ro.sql`](../../clickhouse/users/ro.sql) | users | `SELECT` for `role_<database>_ro` — see [Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse) |

`CREATE OR REPLACE VIEW` makes a change to the query harmless to re-run: the next migration replaces the view in
place, and [`positions`](positions.md), which selects from it, follows on its next refresh. The views
phase runs after the incremental scripts, so `position_events` exists by then, and before `post/`, where `positions`
is created over this view.

The suites in `Execution/Positions/` cover the view alongside the table and `positions` — see
[position_events → Scripts](position_events.md#scripts).

---

## See also

[`position_events`](position_events.md) · [`positions`](positions.md) · [Schema Reference](../SCHEMA_REFERENCE.md) ·
[Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse)
