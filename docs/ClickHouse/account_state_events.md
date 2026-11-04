<div align="center">

# account_state_events

**The trading account states of every sender as a change stream — one row per change, plus the markers that bracket each full snapshot**

ClickHouse · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Engine and keys](#engine-and-keys) · [Retention](#retention) · [Reading it](#reading-it) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | ClickHouse |
| Default name | `account_state_events` — renamed with `--variables account_state_events_table=…` |
| Written by | The trade server |
| One row per | Change of a trading account's state — `Upsert` or `Delete` — or a `SnapshotStart` / `SnapshotEnd` marker |
| Writes | Append-only; the same row can arrive more than once |
| Versioned by | `ReplacingMergeTree(sequence)` on `(sender_id, trading_account_id, marker_sequence)`, when parts merge |
| Retention | `Delete` rows only, 30 days by default, by `batch_time` — `--variables account_state_events_delete_ttl_days=…`; relies on the forced merges, every 600 seconds by default — `--variables account_state_events_min_age_to_force_merge_seconds=…` |
| Read through | [`account_states_live`](account_states_live.md), computed on every read, and [`account_states`](account_states.md), the same rows refreshed on a schedule — one row per trading account |
| Grants | `exporter`: `INSERT` · `ro`: `SELECT` |

The table is the raw stream: every version of every account state the server has sent, until the merges collapse
them, and the snapshot markers the current state is computed from. Most readers want one of the two views over it
instead, which apply the snapshot protocol and return each account once:
[`account_states_live`](account_states_live.md), always current, or [`account_states`](account_states.md),
cheaper to read and up to a refresh period old — see
[Which one to read](account_states_live.md#which-one-to-read). Read this table for the history of an account's state, or to see what a sender has sent.

The protocol is the one of [`position_events`](position_events.md), with `trading_account_id` in the role of
`position_id`:

| `event_type` | Meaning | `trading_account_id` |
|--------------|---------|----------------------|
| `Upsert` | The account state as it now stands | the account |
| `Delete` | The trading account was deleted; the row carries its last known state | the account |
| `SnapshotStart` | A full snapshot of the sender's account states begins | `0` |
| `SnapshotEnd` | The snapshot is complete | `0` |

Between the two markers of a snapshot lies an `Upsert` for every account at the time. What the
markers mean to a reader — the watermark — is explained on [`account_states_live`](account_states_live.md#the-snapshot-protocol).

There is no cursor table on ClickHouse, and the exporter does nothing but insert.

---

## Final SQL

The table as it stands once the two scripts that build it have run, on a single instance with the default name and
the defaults: a 30-day retention of `Delete` rows and a 600-second merge age. The `CREATE TABLE` comes from the
incremental script; `min_age_to_force_merge_seconds` and the `TTL` are laid on afterwards by the re-runnable
`post/account_state_events_ttl.sql`, so that a deployment can change either without a new script.

```sql
CREATE TABLE account_state_events
(
    -- service columns
    sender_id                                UInt64                                                                       NOT NULL,
    sequence                                 UInt64                                                                       NOT NULL,
    batch_time                               DateTime64(6, 'UTC')                                                         NOT NULL,
    event_type                               Enum8('Upsert' = 0, 'Delete' = 1, 'SnapshotStart' = 2, 'SnapshotEnd' = 3) NOT NULL,

    trading_account_id                       UInt64                                                                       NOT NULL,
    group_id                                 UInt64                                                                       NOT NULL,
    account_currency                         LowCardinality(String)                                                       NOT NULL,
    account_currency_decimal_places          UInt8                                                                        NOT NULL,

    balance                                  Decimal(38, 12)                                                              NOT NULL,
    credit                                   Decimal(38, 12)                                                              NOT NULL,
    equity                                   Decimal(38, 12)                                                              NOT NULL,
    margin                                   Decimal(38, 12)                                                              NOT NULL,
    otc_floating_pl                          Decimal(38, 12)                                                              NOT NULL,
    otc_realized_pl                          Decimal(38, 12)                                                              NOT NULL,
    collateral                               Decimal(38, 12)                                                              NOT NULL,
    is_margin_call                           Bool                                                                         NOT NULL,

    -- the sequence for a snapshot marker, 0 for an account; never written by the exporter
    marker_sequence                          UInt64 MATERIALIZED if(trading_account_id = 0, sequence, 0)
)
ENGINE = ReplacingMergeTree(sequence)
PARTITION BY sender_id
ORDER BY (sender_id, trading_account_id, marker_sequence)
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
| `sender_id` | `UInt64` | The trade server instance. Every server exporting into one database must use a different value, or their rows overwrite each other |
| `sequence` | `UInt64` | The version of the change within its sender: of the rows with the same key, the engine keeps the one with the highest `sequence` |
| `batch_time` | `DateTime64(6, 'UTC')` | When the batch that carried the row was created. Every row of one insert has the same value. The age the retention of `Delete` rows is measured by |
| `event_type` | `Enum8` | `Upsert`, `Delete`, `SnapshotStart` or `SnapshotEnd` — see [Overview](#overview) |
| `trading_account_id` | `UInt64` | The trading account, unique within its sender. `0` on a snapshot marker |
| `group_id` | `UInt64` | The account's group. `0` on a snapshot marker |
| `account_currency` | `LowCardinality(String)` | Account currency name |
| `account_currency_decimal_places` | `UInt8` | Decimal places of the account currency: how many of the 12 scale digits of the money columns carry meaning |
| `balance`, `credit`, `equity`, `margin` | `Decimal(38, 12)` | Current state |
| `otc_floating_pl` | `Decimal(38, 12)` | Unrealised profit or loss of the open positions (`pl` in the public API) |
| `otc_realized_pl` | `Decimal(38, 12)` | Realised profit or loss (`r` in the public API) |
| `collateral` | `Decimal(38, 12)` | Market value of collateral adjusted by liquidity margin rate (`a` in the public API) |
| `is_margin_call` | `Bool` | Margin call status |
| `marker_sequence` | `UInt64 MATERIALIZED` | `sequence` on a snapshot marker, `0` on an account. Computed by ClickHouse, never written by the exporter — see [Engine and keys](#engine-and-keys) |

Every column is `NOT NULL`. The four service columns come first. On a snapshot marker only the service columns and
`trading_account_id = 0` carry meaning.

Exchange-related state — assets, liabilities, the exchange figures and the required margin — is deliberately not
exported. It comes with exchange symbols later, as a schema migration that adds the columns.

> **Important:** The column types must match what the exporter writes exactly. The native protocol
> refuses a block whose column type differs from the table's, so a table adopted with, say, `Decimal(18, 8)` where
> the script has `Decimal(38, 12)` refuses every batch. `marker_sequence` is `MATERIALIZED`, so it is not part of the
> insert and the exporter does not know about it.

`marker_sequence` is not returned by `SELECT *`; name it to read it.

---

## Engine and keys

| Choice | Why |
|--------|-----|
| `ReplacingMergeTree(sequence)` | Each account is one row in the end: of the rows with the same sorting key, the merge keeps the one with the highest `sequence`, so a row with a lower `sequence` never replaces one with a higher, whatever order they arrive in |
| `ORDER BY (sender_id, trading_account_id, marker_sequence)` | For an account `marker_sequence` is `0`, so the key is the account itself. Every marker has `trading_account_id = 0`; `marker_sequence` gives each its own key, so markers never replace each other |
| `PARTITION BY sender_id` | One partition per sender keeps the senders apart, so `FINAL` can work one partition at a time — see [`account_states_live`](account_states_live.md#final-sql) |
| `min_age_to_force_merge_seconds` (600) | The parts of a partition are force-merged once every one of them is `account_state_events_min_age_to_force_merge_seconds` seconds old — ten minutes by default — with or without new inserts, so the versions of one account do not stay apart for long. The `Delete` retention depends on it — see [The TTL relies on the forced merges](#the-ttl-relies-on-the-forced-merges) |

The sorting key and `sequence` decide what collapses. Two rows of one account collapse to the one with the higher
`sequence`, whatever their `event_type`: a `Delete` replaces the `Upsert`s before it, and an `Upsert` with a higher
`sequence` replaces a `Delete`. Two rows with the same key and the same `sequence` — one change that arrived twice — are one change,
and the merge keeps the copy inserted last.

ClickHouse has no unique index, so a duplicate or an older version is not rejected on insert: every version exists
until a background merge collapses them, and a plain read returns them all. The exporter's account inserts with
`optimize_on_insert = 0`, so not even two versions inside one insert are collapsed on the way in — see
[Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse). With `--cluster`, the replicated engine may already
drop an insert block identical to one it has seen. See
[Deduplication](../SCHEMA_REFERENCE.md#deduplication).

---

## Retention

`Delete` rows older than `account_state_events_delete_ttl_days` days, by `batch_time`, are removed. The default is 30; a
deployment sets its own with `--variables account_state_events_delete_ttl_days=N`, which changes the processed content of
`post/account_state_events_ttl.sql`, so the script re-runs and the new TTL replaces the old one. `N` must be a positive integer.

Every other row stays:

- An `Upsert` is not removed by age: the next change of its account replaces it, and an account must keep its
  last row however long its state stays unchanged.
- The snapshot markers are what the [`account_states_live`](account_states_live.md#the-snapshot-protocol)
  watermark is computed from.

### The TTL relies on the forced merges

Removing a `Delete` row is safe only once a merge has collapsed it with the earlier versions of its account. Until
then the account's last `Upsert` still sits in another part, and `FINAL` hides it only because the `Delete` row
has the higher `sequence`. If the TTL removed the `Delete` row first, that `Upsert` would win again and the deleted
account would come back in [`account_states_live`](account_states_live.md) and [`account_states`](account_states.md).

Two settings work together to rule that out:

| Setting | Role |
|---------|------|
| `account_state_events_min_age_to_force_merge_seconds` (600) | Guarantees the collapse. Once every part of a partition is this many seconds old — ten minutes by default — ClickHouse merges them whether or not new rows arrive: the merge scheduler wakes on its own timer, not on inserts. Without it the size-based merge selection may leave a small part holding one `Delete` row beside a large part for ever |
| `account_state_events_delete_ttl_days` (30) | Waits for the collapse. Thirty days is not how long a merge takes — that is minutes — but a margin far above it, so a `Delete` row is only ever removed from a part that has long been merged with its account's earlier versions |

So the TTL is safe because of the forced merges, not on its own. Keep both: dropping
`min_age_to_force_merge_seconds` makes any retention unsafe, and a retention cut down towards the time the merges
can fall behind — a server down, merges stopped, the disk too full to merge — removes the margin.

### Setting the merge age

A deployment sets the merge age with `--variables account_state_events_min_age_to_force_merge_seconds=N`; `N` must be a
positive integer. `post/account_state_events_ttl.sql` applies it with
`ALTER TABLE … MODIFY SETTING min_age_to_force_merge_seconds = N`, just before the `TTL`, so the table never carries
the `TTL` without the forced merges. A new value changes the script's processed content, so it re-runs and reaches the
existing table.

The migrator keeps the margin from shrinking away: it refuses a run in which `account_state_events_delete_ttl_days`,
counted in seconds, is less than ten times `account_state_events_min_age_to_force_merge_seconds`, before any script
runs — the same rule as for [`position_events`](position_events.md#setting-the-merge-age), checked for each table on
its own.

| `account_state_events_delete_ttl_days` | Longest `account_state_events_min_age_to_force_merge_seconds` |
|----------------------------------------|---------------------------------------------------------------|
| 1 | 8,640 (2.4 hours) |
| 7 | 60,480 (16.8 hours) |
| 30 (default) | 259,200 (3 days) |

Ten times is the floor the migrator enforces, not a recommendation: the default pair, 30 days over 600 seconds, keeps
a margin more than 4,000 times the merge age. A smaller merge age merges sooner but more often, so a busy table pays
for it in merge load; a larger one keeps more unmerged versions for `FINAL` to sort out on every read.

```bash
# merge every five minutes, keep Delete rows for a week
--variables account_state_events_min_age_to_force_merge_seconds=300 account_state_events_delete_ttl_days=7

# refused: 1 day is 86,400 seconds, less than 10 × 10,000
--variables account_state_events_min_age_to_force_merge_seconds=10000 account_state_events_delete_ttl_days=1
```

The age is counted from `batch_time`, not from the moment the row arrived, and the margin shrinks by however late a
row arrives:

> **Warning:** A `Delete` row whose `batch_time` is already older than the retention when it arrives is removed by the
> TTL before any merge can collapse the account into it, and the account's last `Upsert` comes back. The same holds
> for an `Upsert` that arrives again after its account's `Delete` row has expired: it has nothing left to lose against. `batch_time`
> must be the time the batch was sent, never an older one carried over from the account.

> **Warning:** Keep `account_state_events_delete_ttl_days` far below 28,976. `toDateTime(batch_time) + INTERVAL N DAY` cannot
> reach past 2106, so a larger value wraps the expiry date into the past and the next TTL pass drops every `Delete`
> row. Validation accepts any positive number.

---

## Reading it

- For the current account states read [`account_states_live`](account_states_live.md) or
  [`account_states`](account_states.md), not this table. Each has already applied `FINAL`, dropped the `Delete` rows and the markers, and hidden what the latest complete snapshot no
  longer holds.
- Add `FINAL` to see only the latest version of each account. Without it every version not yet merged away is
  returned.
- Filter on `trading_account_id != 0`, or on `event_type`, to leave the snapshot markers out.
- Filter on `sender_id`: it is the partition and the start of the sorting key. Pair every id with it — see
  [Every id is scoped to its sender](../SCHEMA_REFERENCE.md#every-id-is-scoped-to-its-sender).
- The history of one account is its rows without `FINAL`, by `sequence` — as far as the merges have not already
  collapsed it, which `min_age_to_force_merge_seconds` makes a matter of minutes by default. This table is not an audit log.

```sql
-- the latest version of one account, Delete included
SELECT event_type, sequence, batch_time, balance, equity, margin, is_margin_call
FROM account_state_events FINAL
WHERE sender_id = 1
  AND trading_account_id = 1001;

-- the snapshots of one sender, newest first
SELECT event_type, sequence, batch_time
FROM account_state_events
WHERE sender_id = 1
  AND trading_account_id = 0
ORDER BY sequence DESC
LIMIT 10;
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`clickhouse/20261104_01_create_account_state_events.sql`](../../clickhouse/20261104_01_create_account_state_events.sql) | incremental | The table: columns, engine, partitioning, sorting key |
| [`clickhouse/post/account_state_events_ttl.sql`](../../clickhouse/post/account_state_events_ttl.sql) | post | `min_age_to_force_merge_seconds`, then the `TTL` on `Delete` rows. Re-runs when `account_state_events_min_age_to_force_merge_seconds` or `account_state_events_delete_ttl_days` changes |
| [`clickhouse/users/exporter.sql`](../../clickhouse/users/exporter.sql), [`clickhouse/users/ro.sql`](../../clickhouse/users/ro.sql) | users | `INSERT` for `role_<database>_exporter`, `SELECT` for `role_<database>_ro` — see [Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse) |

The views over the table are created later in the run: [`account_states_live`](account_states_live.md) by
`views/account_states_live.sql`, and [`account_states`](account_states.md) over it by `post/account_states.sql`.

The suites in `Execution/AccountStates/` pin the table and both views down as ClickHouse 26.7 behaves under this DDL —
`AccountStatesMigrationIntegrationTests` for the definitions, re-runs, the live view replaced on a forced re-run with
`account_states` following it, retention, refresh and grants; `AccountStatesDataIntegrationTests` for what happens to
the accounts and what the views show, every state asserted on both views — `account_states` refreshed first —
including a change that the live view shows at once and `account_states` after its next refresh. Each test is named
for the workflow, edge case or invariant it covers, so the suites read as a conformance list.

---

## See also

[`account_states_live`](account_states_live.md) · [`account_states`](account_states.md) ·
[`position_events`](position_events.md) · [Schema Reference](../SCHEMA_REFERENCE.md) ·
[ClickHouse Provider](../CLICKHOUSE_PROVIDER.md) · [Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse)
