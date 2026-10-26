<div align="center">

# positions

**The open positions of every sender as a change stream — one row per change, plus the markers that bracket each full snapshot**

ClickHouse · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Engine and keys](#engine-and-keys) · [Retention](#retention) · [Reading it](#reading-it) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | ClickHouse |
| Default name | `positions` — renamed with `--variables positions_table=…` |
| Written by | The trade server's `position` pipeline — `"entity": "position"`, `"target": "clickhouse"` — over the native protocol |
| One row per | Change of an open position — `Upsert` or `Delete` — or a `SnapshotStart` / `SnapshotEnd` marker |
| Delivery | Append-only, at-least-once |
| Versioned by | `ReplacingMergeTree(sequence)` on `(sender_id, position_id, marker_sequence)`, when parts merge |
| Retention | `Delete` rows only, 30 days by default, by `batch_time` — `--variables positions_delete_ttl_days=…` |
| Read through | [`positions_state_live`](positions_state_live.md), computed on every read, and [`positions_state`](positions_state.md), the same rows refreshed on a schedule — one row per open position |
| Grants | `exporter`: `INSERT` · `ro`: `SELECT` |

The table is the raw stream: every version of every position the server has sent, until the merges collapse them,
and the snapshot markers the current state is computed from. Most readers want one of the two views over it
instead, which apply the snapshot protocol and return each open position once:
[`positions_state_live`](positions_state_live.md), always current, or [`positions_state`](positions_state.md),
cheaper to read and up to a refresh period old — see
[Which one to read](positions_state_live.md#which-one-to-read). Read this table for the history of a position, or to see what a sender has sent.

| `event_type` | Meaning | `position_id` |
|--------------|---------|---------------|
| `Upsert` | The position as it now stands | the position |
| `Delete` | The position was closed; the row carries its last known values | the position |
| `SnapshotStart` | A full snapshot of the sender's open positions begins | `0` |
| `SnapshotEnd` | The snapshot is complete | `0` |

Between the two markers of a snapshot the server sends an `Upsert` for every position it holds open. What the
markers mean to a reader — the watermark — is explained on [`positions_state_live`](positions_state_live.md#the-snapshot-protocol).

There is no cursor table on ClickHouse: a ClickHouse pipeline keeps its export position on disk, and the exporter
does nothing but insert.

---

## Final SQL

The table as it stands once the two scripts that build it have run, on a single instance with the default name and
the default 30-day retention of `Delete` rows. The `CREATE TABLE` comes from the incremental script; the `TTL` is
laid on afterwards by the re-runnable `post/positions_ttl.sql`, so that a deployment can change the retention
without a new script.

```sql
CREATE TABLE positions
(
    -- service columns
    sender_id                       UInt64                                                                     NOT NULL,
    sequence                        UInt64                                                                     NOT NULL,
    batch_time                      DateTime64(6, 'UTC')                                                       NOT NULL,
    event_type                      Enum8('Upsert' = 0, 'Delete' = 1, 'SnapshotStart' = 2, 'SnapshotEnd' = 3) NOT NULL,

    position_id                     UInt64                                                                     NOT NULL,
    trading_account_id              UInt64                                                                     NOT NULL,
    group_id                        UInt64                                                                     NOT NULL,
    group_name                      LowCardinality(String)                                                     NOT NULL,
    symbol_id                       UInt64                                                                     NOT NULL,
    symbol_name                     LowCardinality(String)                                                     NOT NULL,

    side                            Enum8('buy' = 0, 'sell' = 1)                                               NOT NULL,
    volume                          Decimal(18, 8)                                                             NOT NULL,
    lot_size                        Float64                                                                    NOT NULL,
    avg_open_price                  Decimal(18, 8)                                                             NOT NULL,

    stop_loss                       Decimal(18, 8)                                                             NOT NULL,
    stop_loss_order_id              UInt64                                                                     NOT NULL,
    take_profit                     Decimal(18, 8)                                                             NOT NULL,
    take_profit_order_id            UInt64                                                                     NOT NULL,

    close_market_price              Decimal(18, 8)                                                             NOT NULL,
    close_group_markup              Int32                                                                      NOT NULL,
    pl                              Decimal(38, 8)                                                             NOT NULL,
    margin                          Decimal(38, 8)                                                             NOT NULL,
    asset_or_liability              Decimal(38, 8)                                                             NOT NULL,
    profit_conversion_rate          Float64                                                                    NOT NULL,
    margin_conversion_rate          Float64                                                                    NOT NULL,

    swaps                           Decimal(38, 8)                                                             NOT NULL,
    commission                      Decimal(38, 8)                                                             NOT NULL,
    fee                             Decimal(38, 8)                                                             NOT NULL,

    price_decimal_places            UInt8                                                                      NOT NULL,
    account_currency_decimal_places UInt8                                                                      NOT NULL,

    created                         DateTime64(6, 'UTC')                                                       NOT NULL,
    created_by_order_id             UInt64                                                                     NOT NULL,
    updated                         DateTime64(6, 'UTC')                                                       NOT NULL,
    updated_by_order_id             UInt64                                                                     NOT NULL,

    -- the sequence for a snapshot marker, 0 for a position; never written by the exporter
    marker_sequence                 UInt64 MATERIALIZED if(position_id = 0, sequence, 0)
)
ENGINE = ReplacingMergeTree(sequence)
PARTITION BY sender_id
ORDER BY (sender_id, position_id, marker_sequence)
TTL toDateTime(batch_time) + INTERVAL 30 DAY DELETE WHERE event_type = 'Delete'
SETTINGS min_age_to_force_merge_seconds = 600;
```

With `--cluster my_cluster` the statements run `ON CLUSTER 'my_cluster'` and the engine is
`ReplicatedReplacingMergeTree(sequence)`; everything else is the same.

`SHOW CREATE TABLE` prints the same definition in ClickHouse's canonical form — `toIntervalDay(30)` for
`INTERVAL 30 DAY`, and no `DELETE`, which is the default TTL action — and adds the default
`index_granularity = 8192`.

---

## Columns

| Column | Type | Notes |
|--------|------|-------|
| `sender_id` | `UInt64` | The trade server instance, from the pipeline's `sender_id`. Every server exporting into one database must use a different value, or their rows overwrite each other |
| `sequence` | `UInt64` | The order of the change within its sender. Grows monotonically per sender; the version the engine keeps the highest of |
| `batch_time` | `DateTime64(6, 'UTC')` | When the batch that carried the row was created. Every row of one insert has the same value. The age the retention of `Delete` rows is measured by |
| `event_type` | `Enum8` | `Upsert`, `Delete`, `SnapshotStart` or `SnapshotEnd` — see [Overview](#overview) |
| `position_id` | `UInt64` | The position, unique within its sender. `0` on a snapshot marker |
| `trading_account_id` | `UInt64` | The account the position belongs to |
| `group_id` | `UInt64` | The account's group |
| `group_name` | `LowCardinality(String)` | Name of the account's group |
| `symbol_id` | `UInt64` | The instrument's identifier |
| `symbol_name` | `LowCardinality(String)` | Name of the instrument |
| `side` | `Enum8('buy' = 0, 'sell' = 1)` | Direction of the position |
| `volume` | `Decimal(18, 8)` | Volume of the position |
| `lot_size` | `Float64` | Lot size of the instrument |
| `avg_open_price` | `Decimal(18, 8)` | Average open price of the position |
| `stop_loss` | `Decimal(18, 8)` | Stop-loss price |
| `stop_loss_order_id` | `UInt64` | The stop-loss order |
| `take_profit` | `Decimal(18, 8)` | Take-profit price |
| `take_profit_order_id` | `UInt64` | The take-profit order |
| `close_market_price` | `Decimal(18, 8)` | Market price the position would close at |
| `close_group_markup` | `Int32` | Group markup on the close price |
| `pl` | `Decimal(38, 8)` | Profit or loss of the position |
| `margin` | `Decimal(38, 8)` | Margin of the position |
| `asset_or_liability` | `Decimal(38, 8)` | Asset or liability of the position |
| `profit_conversion_rate` | `Float64` | Rate converting profit into the account currency |
| `margin_conversion_rate` | `Float64` | Rate converting margin into the account currency |
| `swaps` | `Decimal(38, 8)` | Swaps accumulated |
| `commission` | `Decimal(38, 8)` | Commission |
| `fee` | `Decimal(38, 8)` | Fees |
| `price_decimal_places` | `UInt8` | Decimal places of the instrument's price |
| `account_currency_decimal_places` | `UInt8` | Decimal places of the account currency |
| `created` | `DateTime64(6, 'UTC')` | When the position was opened |
| `created_by_order_id` | `UInt64` | The order that opened the position |
| `updated` | `DateTime64(6, 'UTC')` | When the position was last changed |
| `updated_by_order_id` | `UInt64` | The order that last changed it |
| `marker_sequence` | `UInt64 MATERIALIZED` | `sequence` on a snapshot marker, `0` on a position. Computed by ClickHouse, never written by the exporter — see [Engine and keys](#engine-and-keys) |

Every column is `NOT NULL`. The four service columns come first. On a snapshot marker only the service columns and
`position_id = 0` carry meaning.

> **Important:** The column types must match the exporter's `PositionDescriptor` exactly. The native protocol
> refuses a block whose column type differs from the table's, so a table adopted with, say, `Decimal(38, 8)` where
> the script has `Decimal(18, 8)` refuses every batch. `marker_sequence` is `MATERIALIZED`, so it is not part of the
> insert and the exporter does not know about it.

`marker_sequence` is not returned by `SELECT *`; name it to read it.

---

## Engine and keys

| Choice | Why |
|--------|-----|
| `ReplacingMergeTree(sequence)` | Each position is one row in the end: of the rows with the same sorting key, the merge keeps the one with the highest `sequence`, so a re-sent or late batch never overwrites a newer change |
| `ORDER BY (sender_id, position_id, marker_sequence)` | For a position `marker_sequence` is `0`, so the key is the position itself. Every marker has `position_id = 0`; `marker_sequence` gives each its own key, so markers never replace each other |
| `PARTITION BY sender_id` | One partition per sender keeps the senders apart, so `FINAL` can work one partition at a time — see [`positions_state_live`](positions_state_live.md#final-sql) |
| `min_age_to_force_merge_seconds = 600` | The parts of a partition are force-merged once every one of them is ten minutes old, with or without new inserts, so the versions of one position do not stay apart for long. The `Delete` retention depends on it — see [The TTL relies on the forced merges](#the-ttl-relies-on-the-forced-merges) |

The sorting key and `sequence` decide what collapses. Two rows of one position collapse to the one with the higher
`sequence`, whatever their `event_type`: a `Delete` replaces the `Upsert`s before it, and an `Upsert` with a higher
`sequence` replaces a `Delete`. Two rows with the same key and the same `sequence` — a re-sent batch — are one change,
and the merge keeps the copy inserted last.

ClickHouse has no unique index, so a duplicate or an older version is not rejected on insert: every version exists
until a background merge collapses them, and a plain read returns them all. The exporter's account inserts with
`optimize_on_insert = 0`, so not even two versions inside one insert are collapsed on the way in — see
[Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse). With `--cluster`, the replicated engine may already
drop a re-sent batch whose insert block is identical to one it has seen. See
[Deduplication](../SCHEMA_REFERENCE.md#deduplication).

---

## Retention

`Delete` rows older than `positions_delete_ttl_days` days, by `batch_time`, are removed. The default is 30; a
deployment sets its own with `--variables positions_delete_ttl_days=N`, which changes the processed content of
`post/positions_ttl.sql`, so the script re-runs and the new TTL replaces the old one. `N` must be a positive integer.

Every other row stays:

- An `Upsert` is not removed by age: the next change of its position replaces it, and an open position must keep its
  last row however long it stays unchanged.
- The snapshot markers are what the [`positions_state_live`](positions_state_live.md#the-snapshot-protocol)
  watermark is computed from.

### The TTL relies on the forced merges

Removing a `Delete` row is safe only once a merge has collapsed it with the earlier versions of its position. Until
then the position's last `Upsert` still sits in another part, and `FINAL` hides it only because the `Delete` row
has the higher `sequence`. If the TTL removed the `Delete` row first, that `Upsert` would win again and the closed
position would come back in [`positions_state_live`](positions_state_live.md) and [`positions_state`](positions_state.md).

Two settings work together to rule that out:

| Setting | Role |
|---------|------|
| `min_age_to_force_merge_seconds = 600` | Guarantees the collapse. Once every part of a partition is ten minutes old, ClickHouse merges them whether or not new rows arrive: the merge scheduler wakes on its own timer, not on inserts. Without it the size-based merge selection may leave a small part holding one `Delete` row beside a large part for ever |
| `positions_delete_ttl_days` (30) | Waits for the collapse. Thirty days is not how long a merge takes — that is minutes — but a margin far above it, so a `Delete` row is only ever removed from a part that has long been merged with its position's earlier versions |

So the TTL is safe because of the forced merges, not on its own. Keep both: dropping
`min_age_to_force_merge_seconds` makes any retention unsafe, and a retention cut down towards the time the merges
can fall behind — a server down, merges stopped, the disk too full to merge — removes the margin.

The age is counted from `batch_time`, not from the moment the row arrived, and the margin shrinks by however late a
row arrives:

> **Warning:** A `Delete` row whose `batch_time` is already older than the retention when it arrives is removed by the
> TTL before any merge can collapse the position into it, and the position's last `Upsert` comes back. The same holds
> for a batch re-sent after its `Delete` row has expired: its `Upsert` has nothing left to lose against. `batch_time`
> must be the time the batch was sent, never an older one carried over from the position.

> **Warning:** Keep `positions_delete_ttl_days` far below 28,976. `toDateTime(batch_time) + INTERVAL N DAY` cannot
> reach past 2106, so a larger value wraps the expiry date into the past and the next TTL pass drops every `Delete`
> row. Validation accepts any positive number.

---

## Reading it

- For the current open positions read [`positions_state_live`](positions_state_live.md) or
  [`positions_state`](positions_state.md), not this table. Each has already applied `FINAL`, dropped the `Delete` rows and the markers, and hidden what the latest complete snapshot no
  longer holds.
- Add `FINAL` to see only the latest version of each position. Without it every version not yet merged away is
  returned.
- Filter on `position_id != 0`, or on `event_type`, to leave the snapshot markers out.
- Filter on `sender_id`: it is the partition and the start of the sorting key. Pair every id with it — see
  [Every id is scoped to its sender](../SCHEMA_REFERENCE.md#every-id-is-scoped-to-its-sender).
- The history of one position is its rows without `FINAL`, by `sequence` — as far as the merges have not already
  collapsed it, which `min_age_to_force_merge_seconds` makes a matter of minutes. This table is not an audit log.

```sql
-- the latest version of one position, Delete included
SELECT event_type, sequence, batch_time, volume, avg_open_price, pl
FROM positions FINAL
WHERE sender_id = 1
  AND position_id = 42;

-- the snapshots of one sender, newest first
SELECT event_type, sequence, batch_time
FROM positions
WHERE sender_id = 1
  AND position_id = 0
ORDER BY sequence DESC
LIMIT 10;
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`clickhouse/20261023_01_create_positions.sql`](../../clickhouse/20261023_01_create_positions.sql) | incremental | The table: columns, engine, partitioning, sorting key, `min_age_to_force_merge_seconds` |
| [`clickhouse/post/positions_ttl.sql`](../../clickhouse/post/positions_ttl.sql) | post | The `TTL` on `Delete` rows. Re-runs when `positions_delete_ttl_days` changes |
| [`clickhouse/users/exporter.sql`](../../clickhouse/users/exporter.sql), [`clickhouse/users/ro.sql`](../../clickhouse/users/ro.sql) | users | `INSERT` for `role_<database>_exporter`, `SELECT` for `role_<database>_ro` — see [Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse) |

The views over the table are created later in the run: [`positions_state_live`](positions_state_live.md) by
`views/positions_state_live.sql`, and [`positions_state`](positions_state.md) over it by `post/positions_state.sql`.

The suites in `Execution/Positions/` pin the table and both views down as ClickHouse 26.7 behaves under this DDL —
`PositionsMigrationIntegrationTests` for the definitions, re-runs, the live view replaced on a forced re-run with
`positions_state` following it, retention, refresh and grants; `PositionsDataIntegrationTests` for what happens to
the positions and what the views show, every state asserted on both views — `positions_state` refreshed first —
including a change that the live view shows at once and `positions_state` after its next refresh. Each test is named for
the workflow, edge case or invariant it covers, so the suites read as a conformance list.

---

## See also

[`positions_state_live`](positions_state_live.md) · [`positions_state`](positions_state.md) ·
[Schema Reference](../SCHEMA_REFERENCE.md) · [ClickHouse Provider](../CLICKHOUSE_PROVIDER.md) ·
[Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse)
