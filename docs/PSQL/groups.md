<div align="center">

# groups

**The current configuration of every live group — derived from `group_events` by a trigger**

PostgreSQL · maintained by the migrator's trigger, never by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Projection](#projection) · [groups_resync](#groups_resync) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `groups` — renamed with `--variables groups_table=…` |
| Written by | The trigger `trg_project_groups` on [`group_events`](group_events.md), in the same transaction as the event |
| One row per | Live group, keyed by `(sender_id, group_id)` |
| Key | `PRIMARY KEY (sender_id, group_id)` |
| Grants | `exporter`: `INSERT`, `SELECT`, `UPDATE`, `DELETE` — the trigger runs as the exporter · `ro`: `SELECT` |
| Companion view | [`groups_resync`](#groups_resync) — whether a resync is in progress, per sender |

"Which groups are live and how is each configured" is a single read here. The table is **not written by the trade
server**: it is derived from the event log by a trigger inside the same transaction as the event that caused the
change, so it cannot lag behind the log, and a batch that fails to commit leaves no trace in it.

`group_id` is the identifier [`trading_accounts`](trading_accounts.md), [`trades`](trades.md), the
[daily statements](daily_statements_export.md) and both snapshot tables carry under that name, so an account or a
trade joins to its group directly — on `sender_id` **and** `group_id`. Both halves are required: each trade server
instance numbers its own groups, so group `7` on one instance and group `7` on another can be two different groups.

```sql
SELECT a.sender_id, a.trading_account_id, g.name, g.currency, g.stopout, g.margin_call
FROM trading_accounts a
         LEFT JOIN groups g ON g.sender_id = a.sender_id AND g.group_id = a.group_id;
```

`LEFT JOIN`, because the group and account exports are separate pipelines that a deployment enables independently;
joining `trades`, also because a trade outlives its group.

> **Important:** This is the group's **current** configuration, not the one in force when a trade executed or a
> statement was generated. A leverage, stop-out or margin call level changed since then shows as it is now. The
> trade row keeps the group's name as it was, in `group_name`; for the rest of the configuration at an earlier time,
> read `group_events`, within its retention.

Join on the id, not on `name`. There is deliberately **no unique key** on `(sender_id, name)`: between a resync's
start and end markers, a group deleted while the sender was down and a new one created under its name both hold a
row, and a unique index would turn the re-announcement of the new one into a constraint violation that stalls the
export.

---

## Final SQL

The table and its companion view as the migrator creates them, with the default names substituted. The projection
function and trigger are under [Projection](#projection).

```sql
CREATE TABLE groups
(
    sender_id                     BIGINT                   NOT NULL,
    group_id                      BIGINT                   NOT NULL,
    version                       BIGINT                   NOT NULL,
    name                          VARCHAR(128)             NOT NULL,
    company                       VARCHAR(64)              NOT NULL,
    -- nullable as in the log, or a NULL event would stall the export
    email_service_id              BIGINT                   NULL,
    currency                      VARCHAR(16)              NOT NULL,
    currency_decimal_places       INTEGER                  NOT NULL,
    interest_rate                 NUMERIC                  NOT NULL,
    free_margin_mode              INTEGER                  NOT NULL,
    stopout_value_type            INTEGER                  NOT NULL,
    stopout                       NUMERIC                  NOT NULL,
    margin_call_value_type        INTEGER                  NOT NULL,
    margin_call                   NUMERIC                  NOT NULL,
    open_position_type            INTEGER                  NOT NULL,
    exchange_trading_mode         SMALLINT                 NOT NULL,
    default_leverage              INTEGER                  NOT NULL,
    demo_deposit                  NUMERIC                  NOT NULL,
    demo_inactivity_period        INTEGER                  NOT NULL,
    max_trade_history_period      INTEGER                  NOT NULL,
    max_orders                    INTEGER                  NOT NULL,
    max_positions                 INTEGER                  NOT NULL,
    max_market_data_subscriptions INTEGER                  NOT NULL,
    send_daily_reports            BOOLEAN                  NOT NULL,
    send_monthly_reports          BOOLEAN                  NOT NULL,
    symbol_group_profile_count    BIGINT                   NOT NULL,
    commission_profile_count      BIGINT                   NOT NULL,

    -- the event row this state came from
    source_event_row_id           BIGINT                   NOT NULL,
    -- when the projection last wrote the row
    time_applied                  TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),

    PRIMARY KEY (sender_id, group_id)
);

CREATE VIEW groups_resync AS
SELECT sender_id,
       max(id) FILTER (WHERE event_type = 2)            AS resync_started_event_row_id,
       max(time_inserted) FILTER (WHERE event_type = 2) AS time_resync_started,
       max(time_inserted) FILTER (WHERE event_type = 3) AS time_resync_finished,
       COALESCE(max(id) FILTER (WHERE event_type = 2), -1) >
       COALESCE(max(id) FILTER (WHERE event_type = 3), -1) AS resync_in_progress
FROM group_events
WHERE event_type IN (2, 3)
GROUP BY sender_id;
```

---

## Columns

Every group column of [`group_events`](group_events.md#columns), with the same types and nullability, in the same
order and with the same meanings, minus the log's own envelope (`id`, `time_inserted`, `event_type`) and plus:

| Column | Type | Notes |
|--------|------|-------|
| `sender_id`, `group_id` | `BIGINT` | `PRIMARY KEY (sender_id, group_id)` |
| `source_event_row_id` | `BIGINT` | The `group_events.id` this state came from. Traceability, and what a resync's end marker compares against |
| `time_applied` | `TIMESTAMP WITH TIME ZONE` | When the projection last wrote the row |

`email_service_id` stays nullable here: a `NOT NULL` would turn the first group without an email service into a
failed trigger, and the failed trigger into a stalled export. None of the columns but `time_applied` has a default.
The enum members — free margin, stop-out and margin call modes, netting or hedging, exchange settlement — are under
[`group_events` → Values](group_events.md#values).

There is deliberately **no foreign key** on `source_event_row_id`, for the same reason as on
[`trading_accounts`](trading_accounts.md#columns): an event ages out of the retention window long before the group
does.

No indexes beyond the primary key. The table is bounded at the live group count, so a filter the primary key cannot
serve, by `name` or `currency` say, is a scan of a small table.

---

## Projection

[`psql/views/groups_projection.sql`](../../psql/views/groups_projection.sql) creates the function `project_groups()` and this trigger on the log:

```sql
CREATE TRIGGER trg_project_groups
    AFTER INSERT
    ON group_events
    FOR EACH ROW
EXECUTE FUNCTION project_groups();
```

It behaves exactly as [the trading account projection](trading_accounts.md#projection) does, against the group
tables:

| `event_type` | What the trigger does |
|--------------|-----------------------|
| `0` group state | Upserts the group's row, stamped with the event's `id` |
| `1` deletion | Deletes the group's row |
| `2` resync started | **Nothing.** The row's own `id` is the watermark the end marker reads back out of the log |
| `3` resync finished | Deletes that sender's rows stamped older than the newest `2` before it |
| anything else | Nothing — stored in the log, not projected, and no batch fails |

The script is re-runnable and ends with the same reconcile pass, which also applies a resync sweep the trigger missed
because it was absent when the end marker arrived — see
[Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections).

> **Note:** The projection names every column in four places — the `INSERT` and the `ON CONFLICT` `SET` in the
> function, then both again in the reconcile. The guard is a test: *Invariant Should copy every field from the event
> unchanged on all four write paths* in `GroupsProjectionIntegrationTests` drives each of the four in turn and
> compares the whole log row against the whole state row as `jsonb`. `email_service_id` turns from a value to `NULL`
> and back on every path, so the test also fails on a `COALESCE` that would keep a removed service.

---

## groups_resync

The same answer as [`trading_accounts_resync`](trading_accounts.md#trading_accounts_resync), for the group export,
aggregated from the resync markers in [`group_events`](group_events.md#event-types) — the same columns with the same
meanings. It is a separate view because the two exports are separate pipelines: a sender can be mid-resync on
groups while its trading account picture is settled, and vice versa.

| | |
|---|---|
| Default name | `groups_resync` — renamed with `--variables groups_resync_view=…` |
| Grants | `ro`: `SELECT` · `exporter`: none |

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20261007_02_create_groups.sql`](../../psql/20261007_02_create_groups.sql) | incremental | The table |
| [`psql/views/groups_projection.sql`](../../psql/views/groups_projection.sql) | views | `project_groups()`, `trg_project_groups` and the reconcile pass |
| [`psql/views/groups_resync.sql`](../../psql/views/groups_resync.sql) | views | The `groups_resync` view |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`group_events`](group_events.md) · [`trading_accounts`](trading_accounts.md)
