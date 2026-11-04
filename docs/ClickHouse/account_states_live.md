<div align="center">

# account_states_live

**The trading account states of every sender, one row each, computed on every read — a view over `account_state_events`**

ClickHouse · view · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [The snapshot protocol](#the-snapshot-protocol) · [Which one to read](#which-one-to-read) · [Reading it](#reading-it) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | ClickHouse |
| Kind | Plain view over [`account_state_events`](account_state_events.md), `SQL SECURITY INVOKER` |
| Default name | `account_states_live` — renamed with `--variables account_states_live_view=…` |
| One row per | Trading account of every sender, as of the latest complete snapshot and the changes after it |
| Columns | Every column of `account_state_events` except the `MATERIALIZED` `marker_sequence` |
| Freshness | Always current: the query runs on every read |
| Grants | `ro`: `SELECT`, which also needs `SELECT` on `account_state_events` · `exporter`: none |

[`account_state_events`](account_state_events.md) is a change stream: every version of every account state until the
merges collapse them, the `Delete` rows of deleted accounts, and the markers of each snapshot. This view turns it into
the current state. It reads the table with `FINAL`, keeps the latest version of each account, drops the deleted ones
and the markers, and hides every account the latest complete snapshot no longer holds.

This is **the one definition of the state**. [`account_states`](account_states.md) stores the same rows on a
schedule by selecting from this view, so a change to the query here reaches it on its next refresh.

The trade server never reads or writes it. The view name is templated so that a database already owning an object
called `account_states_live` can move ours aside.

---

## Final SQL

The view as the migrator creates it, under the default names.

```sql
CREATE OR REPLACE VIEW account_states_live
AS
WITH
    snapshot_ends AS
    (
        SELECT sender_id, max(sequence) AS end_sequence
        FROM account_state_events
        WHERE trading_account_id = 0 AND event_type = 'SnapshotEnd'
        GROUP BY sender_id
    ),
    watermarks AS
    (
        SELECT starts.sender_id, max(starts.sequence) AS watermark
        FROM account_state_events AS starts
        INNER JOIN snapshot_ends ON starts.sender_id = snapshot_ends.sender_id
        WHERE starts.trading_account_id = 0
          AND starts.event_type = 'SnapshotStart'
          AND starts.sequence < snapshot_ends.end_sequence
        GROUP BY starts.sender_id
    )
SELECT account_state_events.*
FROM account_state_events AS account_state_events FINAL
INNER JOIN watermarks ON account_state_events.sender_id = watermarks.sender_id
WHERE account_state_events.trading_account_id != 0
  AND account_state_events.event_type = 'Upsert'
  AND account_state_events.sequence >= watermarks.watermark
SETTINGS do_not_merge_across_partitions_select_final = 1;
```

With `--cluster my_cluster` the statement runs `ON CLUSTER 'my_cluster'`, and every node's view reads its own
replica of `account_state_events`.

The view runs with the rights of whoever reads it — `SQL SECURITY INVOKER`, the default for a plain view, which
`SHOW CREATE TABLE` does not print. So a reader needs `SELECT` on `account_state_events` as well as on the view.

`do_not_merge_across_partitions_select_final = 1` lets `FINAL` work one partition at a time. `account_state_events` is
partitioned by `sender_id`, which is the start of its sorting key, so no two versions of one account are ever in
different partitions and the result is the same.

---

## Columns

Every column of [`account_state_events`](account_state_events.md#columns), with the same types and meanings, in the same
order, except `marker_sequence` — a `MATERIALIZED` column, which `SELECT account_state_events.*` does not return, and
which is `0` on every row the view keeps.

`event_type` is always `Upsert` and `trading_account_id` is never `0`. `sequence` and `batch_time` are those of the
account's latest change.

---

## The snapshot protocol

`account_state_events` carries an account as an `Upsert` for each change of its state and a `Delete` when the account
is deleted, and full snapshots: a `SnapshotStart`, an `Upsert` for every account at the time, and a `SnapshotEnd`. The
snapshot is what lets the view forget an account whose `Delete` never arrived: an account that is not in the latest
complete snapshot is hidden. The protocol is the one of [`positions_live`](positions_live.md#the-snapshot-protocol),
with `trading_account_id` in the role of `position_id`.

For every sender the view computes a **watermark**: the `sequence` of the latest `SnapshotStart` that has a
`SnapshotEnd` after it. Then:

| Rule | Effect |
|------|--------|
| An account whose latest row is older than the watermark is hidden | It was not part of the latest complete snapshot |
| An account whose latest row is a `Delete` is hidden | It was deleted |
| A sender with no complete snapshot has no watermark | The sender is absent from the view until its first snapshot completes |
| A snapshot still in progress does not move the watermark | Until its `SnapshotEnd` arrives the previous watermark stands, so an incomplete snapshot hides nothing |

The `Upsert`s of a snapshot in progress are changes like any other: one after the current watermark replaces the
account's earlier version at once.

### Worked example

One sender, `sender_id = 1`. Each line is a row of `account_state_events`; the right-hand column is what the view
returns once that row has arrived — and what [`account_states`](account_states.md) holds after its next refresh.

| `sequence` | `event_type` | `trading_account_id` | Watermark | View returns |
|-----------:|--------------|---------------------:|----------:|--------------|
| 1 | `SnapshotStart` | 0 | — | nothing: no complete snapshot, sender 1 absent |
| 2 | `Upsert` | 1001 | — | nothing |
| 3 | `Upsert` | 1002 | — | nothing |
| 4 | `SnapshotEnd` | 0 | 1 | 1001 (seq 2), 1002 (seq 3) |
| 5 | `Upsert` | 1002 | 1 | 1001 (seq 2), 1002 (seq 5) |
| 6 | `Upsert` | 1003 | 1 | 1001, 1002, 1003 (seq 6) |
| 7 | `Delete` | 1001 | 1 | 1002, 1003 — 1001 is deleted |
| 8 | `SnapshotStart` | 0 | 1 | 1002, 1003 — the new snapshot is in progress and changes nothing |
| 9 | `Upsert` | 1002 | 1 | 1002 (seq 9), 1003 (seq 6) |
| 10 | `SnapshotEnd` | 0 | 8 | 1002 (seq 9) — 1003 was not in the snapshot, its row 6 is older than the watermark |

Account 1003 disappears without a `Delete`: the snapshot from 8 to 10 did not contain it. Had another `SnapshotStart`
arrived at 11 and nothing more, the watermark would stay at 8 and the view would not change.

The rules assume that, within a sender, a later row carries a higher `sequence`: the watermark is a comparison of sequences.

---

## Which one to read

The two views return the same rows; they differ in when the work is done.

| | `account_states_live` | [`account_states`](account_states.md) |
|---|------------------------|------------------------------------------|
| Kind | Plain view | Refreshable materialized view over this one |
| Freshness | Current at every read | As old as the last refresh — up to `account_states_refresh_seconds`, 300 by default, plus the time a refresh takes |
| Cost per read | A `FINAL` read of the whole of `account_state_events` | A read of a plain `MergeTree` table |
| Grants a reader needs | `SELECT` on the view and on `account_state_events` | `SELECT` on the view |
| Server version | any the migrator supports | 24.10 or later |

Read this view when a change must be visible as soon as it lands — a support check on one account, a reconciliation
against the server. Read `account_states` for dashboards, reports and anything that queries often.

---

## Reading it

- Read it as a plain table: no `FINAL`, no filter on `event_type` or `trading_account_id`. Every row is a trading account.
- Every read pays for the whole query — `FINAL` over `account_state_events`, and the watermark join. A filter on
  `sender_id` narrows what the outer read returns; do not count on it to make the query cheaper.
- Filter on `sender_id`, and pair every id with it — see
  [Every id is scoped to its sender](../SCHEMA_REFERENCE.md#every-id-is-scoped-to-its-sender). A sender with no
  complete snapshot yet has no rows.

```sql
-- the state of one account, as it stands now
SELECT account_currency, balance, credit, equity, margin, otc_floating_pl, is_margin_call
FROM account_states_live
WHERE sender_id = 1
  AND trading_account_id = 1001;
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`clickhouse/views/account_states_live.sql`](../../clickhouse/views/account_states_live.sql) | views | The view, as `CREATE OR REPLACE VIEW`. Re-runs when its content changes |
| [`clickhouse/users/ro.sql`](../../clickhouse/users/ro.sql) | users | `SELECT` for `role_<database>_ro` — see [Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse) |

`CREATE OR REPLACE VIEW` makes a change to the query harmless to re-run: the next migration replaces the view in
place, and [`account_states`](account_states.md), which selects from it, follows on its next refresh. The views
phase runs after the incremental scripts, so `account_state_events` exists by then, and before `post/`, where
`account_states` is created over this view.

The suites in `Execution/AccountStates/` cover the view alongside the table and `account_states` — see
[account_state_events → Scripts](account_state_events.md#scripts).

---

## See also

[`account_state_events`](account_state_events.md) · [`account_states`](account_states.md) ·
[`positions_live`](positions_live.md) · [Schema Reference](../SCHEMA_REFERENCE.md) ·
[Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse)
