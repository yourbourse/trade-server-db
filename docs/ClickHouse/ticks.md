<div align="center">

# ticks

**The top of the order book of every symbol — the best bid and the best ask, one row per change**

ClickHouse · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Engine and keys](#engine-and-keys) · [Retention](#retention) · [Reading it](#reading-it) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | ClickHouse — the one entity the trade server exports there |
| Default name | `ticks` — renamed with `--variables ticks_table=…` |
| Written by | The trade server's `tick` pipeline — `"entity": "tick"`, `"target": "clickhouse"` — over the native protocol |
| One row per | Change of a symbol's best bid or best ask |
| Delivery | Append-only, at-least-once |
| Deduplicated by | `ReplacingMergeTree` on `(sender_id, short_symbol_name, created_time, sequence)`, when parts merge |
| Retention | 31 days by default, by `created_time` — `--variables ticks_ttl_days=…` |
| Grants | `exporter`: `INSERT` · `ro`: `SELECT` |

The trade server writes a tick when it publishes prices for a symbol and the best bid or the best ask differs from
the previous publication. A change of price and a change of volume both count; a change deeper in the book does not.
Prices include the symbol's markup and not the group markups. A symbol produces at most as many ticks per second as
its `throttling` setting allows — up to 1000 price updates per second in the Admin REST API, where `0` turns
throttling off and every update is published.

There is no cursor table and no view on ClickHouse: a ClickHouse pipeline keeps its export position on disk, and the
exporter does nothing but insert.

---

## Final SQL

The table as it stands once the two scripts that build it have run, on a single instance with the default name and
the default 31-day retention. The `CREATE TABLE` comes from the incremental script; the `TTL` and
`ttl_only_drop_parts` are laid on afterwards by the re-runnable `post/ticks_ttl.sql`, so that a deployment can
change the retention without a new script.

```sql
CREATE TABLE ticks
(
    -- service columns
    sender_id         UInt64                 NOT NULL,
    sequence          UInt64                 NOT NULL,
    batch_time        DateTime64(6, 'UTC')   NOT NULL CODEC(Delta, Default),

    created_time      DateTime64(6, 'UTC')   NOT NULL CODEC(Delta, Default),

    symbol_id         UInt64                 NOT NULL,
    short_symbol_name LowCardinality(String) NOT NULL,

    -- best bid; a zero volume means the side is empty
    bid_price         Decimal128(12)         NOT NULL,
    bid_volume        Decimal64(8)           NOT NULL,

    -- best ask; a zero volume means the side is empty
    ask_price         Decimal128(12)         NOT NULL,
    ask_volume        Decimal64(8)           NOT NULL
)
ENGINE = ReplacingMergeTree
PARTITION BY toYYYYMM(created_time)
PRIMARY KEY (sender_id, short_symbol_name)
ORDER BY (sender_id, short_symbol_name, created_time, sequence)
TTL toDateTime(created_time) + INTERVAL 31 DAY
SETTINGS ttl_only_drop_parts = 1;
```

With `--cluster my_cluster` the statements run `ON CLUSTER 'my_cluster'` and the engine is
`ReplicatedReplacingMergeTree`; everything else is the same.

`SHOW CREATE TABLE` prints the same definition in ClickHouse's canonical form — `Decimal(38, 12)` for
`Decimal128(12)`, `Delta(8)` for `Delta`, `toIntervalDay(31)` for `INTERVAL 31 DAY` — and adds the default
`index_granularity = 8192`.

---

## Columns

| Column | Type | Notes |
|--------|------|-------|
| `sender_id` | `UInt64` | The trade server instance, from the pipeline's `sender_id`. Every server exporting into one database must use a different value, or their rows overwrite each other |
| `sequence` | `UInt64` | The order in which this server instance inserted its rows. Restarts from the beginning with the instance |
| `batch_time` | `DateTime64(6, 'UTC')` | When the batch that carried the row was created. Every row of one insert has the same value |
| `created_time` | `DateTime64(6, 'UTC')` | When the trade server received the latest price update the publication includes |
| `symbol_id` | `UInt64` | Unique identifier of the instrument |
| `short_symbol_name` | `LowCardinality(String)` | Name of the instrument without its path, such as `EURUSD` |
| `bid_price` | `Decimal128(12)` | Best bid price. Zero when the bid side is empty |
| `bid_volume` | `Decimal64(8)` | Volume at the best bid, in the units of the order book. Zero when the bid side is empty |
| `ask_price` | `Decimal128(12)` | Best ask price. Zero when the ask side is empty |
| `ask_volume` | `Decimal64(8)` | Volume at the best ask. Zero when the ask side is empty |

Every column is `NOT NULL`. The three service columns come first, as on every ClickHouse table the trade server
writes; `batch_time` and `created_time` carry `CODEC(Delta, Default)`, because consecutive rows of one symbol are
close in time.

What the types do with a value that does not fit:

- Digits past a decimal column's scale are cut, not rounded: `1.9999999999999` is stored as `1.999999999999`.
- A value outside its column's range — a volume of 10¹⁰ or more, a price of 10²⁶ or more — or a `nan` or `inf`
  price refuses the whole batch, and the exporter is told.
- `NOT NULL` refuses nothing: a `NULL` is stored as zero, so a `NULL` price reads as an empty side.
- An id outside `UInt64`, such as `-1`, is stored wrapped rather than refused.
- Timestamps keep the microsecond and are stored in UTC whatever the writer's session time zone; a seventh
  fractional digit is cut.

---

## Engine and keys

| Choice | Why |
|--------|-----|
| `ReplacingMergeTree` | Delivery is at-least-once: after a lost connection the server re-sends the batch it could not confirm. Duplicates collapse on the sorting key when the parts merge, and they are identical rows, so no version column is needed |
| `PRIMARY KEY (sender_id, short_symbol_name)` | The sparse index held in memory stops at the symbol |
| `ORDER BY (…, created_time, sequence)` | The sorting key goes on to `created_time` and `sequence`, so that two ticks of one symbol never collapse into each other |
| `PARTITION BY toYYYYMM(created_time)` | Monthly partitions, by the UTC month of `created_time`, make the TTL cheap: a part is dropped whole once every row in it has expired |

The sorting key alone decides what collapses. Two rows with the same `(sender_id, short_symbol_name, created_time,
sequence)` are one tick whatever their prices, and the merge keeps the copy inserted last — which is why each server
exporting into one database needs its own `sender_id`.

ClickHouse has no unique index, so unlike PostgreSQL a duplicate is not rejected on insert: both copies exist until
a background merge collapses them. Two copies inside one insert are the exception: the insert collapses them itself.
A re-send that falls in the next month still collapses, because the copy keeps its original's `created_time` and so
its partition. With `--cluster`, the replicated engine may already drop a re-sent batch whose
insert block is identical to one it has seen. See [Deduplication](../SCHEMA_REFERENCE.md#deduplication).

---

## Retention

Rows older than `ticks_ttl_days` days, by `created_time`, are removed. The default is 31; a deployment sets its own
with `--variables ticks_ttl_days=N`, which changes the processed content of `post/ticks_ttl.sql`, so the script
re-runs, the new TTL replaces the old one, and the run applies it to the ticks already stored. `N` must be a
positive integer.

> **Warning:** Keep `ticks_ttl_days` far below 28,976. `toDateTime(created_time) + INTERVAL N DAY` cannot reach past
> 2106, so a larger value wraps the expiry date into the past — `+ INTERVAL 29000 DAY` from a tick of today lands in
> January 1970 — and the next TTL pass drops every tick. Validation accepts any positive number.

`ttl_only_drop_parts = 1` makes ClickHouse drop a part only once **every** row in it has expired, rather than
rewriting parts to cut single rows out. The monthly partitions keep that cheap, at a price: retention is a
minimum, and a tick can outlive it while its part still holds a younger one, until a merge rewrites that part.

Age is `created_time`, not `batch_time`, so a tick re-sent long after it was created still expires on time — and a
tick stamped with a `created_time` already older than the retention is removed by the next TTL pass, silently — a
zero or missing `created_time`, which reads as 1970, included.

---

## Reading it

- Add `FINAL` to read every row once before the parts have merged. It costs more than a plain read; for aggregates
  over large ranges, deduplicate in the query (`argMax` by the key) or accept the small window of duplication.
- Sort by `created_time`, not by `sequence`, to read ticks in time order. `sequence` is numbered per server
  instance and starts again after a restart.
- Filter on `sender_id` and `short_symbol_name`: they are the primary key. A filter on `symbol_id` alone cannot use
  it. Filter on `created_time`, not `batch_time`, to skip whole months.
- A side of the book with no quotes is a zero price **and** a zero volume — check the volume to tell an empty side
  from a price.
- When a symbol's session closes the server writes one tick with both sides empty, stamped with the time of the
  last price update before the close — not the time of the close; it shares `created_time` with that update, and
  `sequence` orders the two. No ticks are written for the symbol while its session stays closed.
- After a restart the first tick of every symbol is written even when it equals the last one before the restart.

```sql
-- the book of one symbol over the last hour, every tick once
SELECT created_time, bid_price, bid_volume, ask_price, ask_volume
FROM ticks FINAL
WHERE sender_id = 1
  AND short_symbol_name = 'EURUSD'
  AND created_time >= now64(6) - INTERVAL 1 HOUR
ORDER BY created_time, sequence;
```

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`clickhouse/20260922_01_create_ticks.sql`](../../clickhouse/20260922_01_create_ticks.sql) | incremental | The table: columns, codecs, engine, partitioning, keys |
| [`clickhouse/post/ticks_ttl.sql`](../../clickhouse/post/ticks_ttl.sql) | post | `ttl_only_drop_parts = 1` and the `TTL`. Re-runs when `ticks_ttl_days` changes |
| [`clickhouse/users/exporter.sql`](../../clickhouse/users/exporter.sql), [`clickhouse/users/ro.sql`](../../clickhouse/users/ro.sql) | users | `INSERT` for `role_<database>_exporter`, `SELECT` for `role_<database>_ro` — see [Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse) |

The definition was taken column for column from the ticks script of the *Export Setup* guide; only the table name,
the cluster clause, the engine and the retention are templated. A table created by hand from the guide is adopted
as it stands, ticks included, and the retention is laid on top; its engine is not converted — see
[`IF NOT EXISTS` everywhere](../SCHEMA_REFERENCE.md#if-not-exists-everywhere).

The suites in `Execution/Ticks/` pin the table down as ClickHouse 26.7 behaves under this DDL —
`TicksMigrationIntegrationTests` for the definition, retention and grants, `TicksDataIntegrationTests` for what
happens to the ticks. Each test is named for the workflow, edge case or invariant it covers, so the suites read as a
conformance list.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [ClickHouse Provider](../CLICKHOUSE_PROVIDER.md) ·
[Users & Permissions](../USERS_AND_PERMISSIONS.md#clickhouse)
