<div align="center">

# symbols

**The current configuration of every live symbol — derived from `symbol_events` by a trigger**

PostgreSQL · maintained by the migrator's trigger, never by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Projection](#projection) · [symbols_resync](#symbols_resync) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `symbols` — renamed with `--variables symbols_table=…` |
| Written by | The trigger `trg_project_symbols` on [`symbol_events`](symbol_events.md), in the same transaction as the event |
| One row per | Live symbol, keyed by `(sender_id, symbol_id)` |
| Key | `PRIMARY KEY (sender_id, symbol_id)` |
| Grants | `exporter`: `INSERT`, `SELECT`, `UPDATE`, `DELETE` — the trigger runs as the exporter · `ro`: `SELECT` |
| Companion view | [`symbols_resync`](#symbols_resync) — whether a resync is in progress, per sender |

"Which symbols are live and how is each configured" is a single read here. The table is **not written by the trade
server**: it is derived from the event log by a trigger inside the same transaction as the event that caused the
change, so it cannot lag behind the log, and a batch that fails to commit leaves no trace in it.

`symbol_id` is the identifier [`trades`](trades.md), [order snapshots](daily_statements_order_snapshots.md) and
[position snapshots](daily_statements_position_snapshots.md) carry under that name, so a trade joins to its symbol
directly — on `sender_id` **and** `symbol_id`. Both halves are required: each trade server instance numbers its own
symbols, so symbol `25` on one instance and symbol `25` on another can be two different instruments.

```sql
SELECT t.sender_id, t.trade_id, t.symbol_name, s.profit_currency, s.margin_calc_mode
FROM trades t
         LEFT JOIN symbols s ON s.sender_id = t.sender_id AND s.symbol_id = t.symbol_id;
```

`LEFT JOIN`, because a trade outlives its symbol: once a symbol is deleted it has no row here.

[`ticks`](../ClickHouse/ticks.md) carries `symbol_id` and `short_symbol_name` too, but it is on ClickHouse, which has no
`symbols` table: putting a symbol's configuration beside its ticks means joining outside the database.

> **Important:** This is the symbol's **current** configuration, not the one in force when a trade executed. A lot
> size, tick value or margin rate changed since then shows as it is now. The trade row already carries the values
> that mattered at execution — `contract_size`, `tick_value`, `tick_size`, `price_decimal_places` — and those are
> the ones to use for that trade. For the configuration at an earlier time, read `symbol_events`, within its
> retention.

Join on the id, not on `symbol_name`. There is deliberately **no unique key** on `(sender_id, symbol_name)`: between
a resync's start and end markers, a symbol deleted while the sender was down and a new one created under its name
both hold a row, and a unique index would turn the re-announcement of the new one into a constraint violation that
stalls the export.

---

## Final SQL

The table and its companion view as the migrator creates them, with the default names substituted. The projection
function and trigger are under [Projection](#projection).

```sql
CREATE TABLE symbols
(
    sender_id                               BIGINT                   NOT NULL,
    symbol_id                               BIGINT                   NOT NULL,

    throttling_price_update_count           INTEGER                  NOT NULL,
    spread_balance                          INTEGER                  NOT NULL,
    decimal_places                          INTEGER                  NOT NULL,
    allow_negative_prices                   BOOLEAN                  NOT NULL,
    allow_realtime_quotes                   BOOLEAN                  NOT NULL,
    market_depth                            INTEGER                  NOT NULL,
    min_spread_filter                       INTEGER                  NOT NULL,
    max_spread_filter                       INTEGER                  NOT NULL,
    soft_filter                             INTEGER                  NOT NULL,
    soft_filter_tick_count                  INTEGER                  NOT NULL,
    hard_filter                             INTEGER                  NOT NULL,
    hard_filter_tick_count                  INTEGER                  NOT NULL,
    discard_filter                          INTEGER                  NOT NULL,
    gap_diff                                INTEGER                  NOT NULL,
    gap_tick_count                          INTEGER                  NOT NULL,

    max_price_limit                         NUMERIC                  NOT NULL,
    min_price_limit                         NUMERIC                  NOT NULL,
    tick_value                              NUMERIC                  NOT NULL,
    tick_size                               NUMERIC                  NOT NULL,
    lot_size                                NUMERIC                  NOT NULL,

    base_currency_decimal_places            INTEGER                  NOT NULL,
    profit_currency_decimal_places          INTEGER                  NOT NULL,
    margin_currency_decimal_places          INTEGER                  NOT NULL,

    trade_mode                              INTEGER                  NOT NULL,
    margin_calc_mode                        INTEGER                  NOT NULL,
    profit_calc_mode                        INTEGER                  NOT NULL,

    min_order_size_lots                     NUMERIC                  NOT NULL,
    max_order_size_lots                     NUMERIC                  NOT NULL,
    order_size_increment_lots               NUMERIC                  NOT NULL,
    max_position_lots                       NUMERIC                  NOT NULL,

    stops_level                             INTEGER                  NOT NULL,
    freeze_level                            INTEGER                  NOT NULL,
    allow_market_orders                     BOOLEAN                  NOT NULL,
    allow_limit_orders                      BOOLEAN                  NOT NULL,
    allow_stop_orders                       BOOLEAN                  NOT NULL,
    allow_stop_limit_orders                 BOOLEAN                  NOT NULL,
    allow_stop_loss_orders                  BOOLEAN                  NOT NULL,
    allow_take_profit_orders                BOOLEAN                  NOT NULL,
    allow_close_by_orders                   BOOLEAN                  NOT NULL,
    chart_mode                              INTEGER                  NOT NULL,
    quotes_timeout_seconds                  INTEGER                  NOT NULL,

    initial_margin                          NUMERIC                  NOT NULL,
    maintenance_margin                      NUMERIC                  NOT NULL,
    hedged_margin                           NUMERIC                  NOT NULL,

    initial_buy_margin_rate                 NUMERIC                  NOT NULL,
    initial_sell_margin_rate                NUMERIC                  NOT NULL,
    initial_buy_limit_margin_rate           NUMERIC                  NOT NULL,
    initial_sell_limit_margin_rate          NUMERIC                  NOT NULL,
    initial_buy_stop_margin_rate            NUMERIC                  NOT NULL,
    initial_sell_stop_margin_rate           NUMERIC                  NOT NULL,
    initial_buy_stop_limit_margin_rate      NUMERIC                  NOT NULL,
    initial_sell_stop_limit_margin_rate     NUMERIC                  NOT NULL,

    maintenance_buy_margin_rate             NUMERIC                  NOT NULL,
    maintenance_sell_margin_rate            NUMERIC                  NOT NULL,
    maintenance_buy_limit_margin_rate       NUMERIC                  NOT NULL,
    maintenance_sell_limit_margin_rate      NUMERIC                  NOT NULL,
    maintenance_buy_stop_margin_rate        NUMERIC                  NOT NULL,
    maintenance_sell_stop_margin_rate       NUMERIC                  NOT NULL,
    maintenance_buy_stop_limit_margin_rate  NUMERIC                  NOT NULL,
    maintenance_sell_stop_limit_margin_rate NUMERIC                  NOT NULL,

    percentage                              NUMERIC                  NOT NULL,

    symbol_name                             VARCHAR(32)              NOT NULL,
    base_currency                           VARCHAR(16)              NOT NULL,
    profit_currency                         VARCHAR(16)              NOT NULL,
    margin_currency                         VARCHAR(16)              NOT NULL,

    margin_use_larger_leg                   BOOLEAN                  NOT NULL,
    recalculate_margin_rate_at_eod          BOOLEAN                  NOT NULL,
    margin_check_on_sl_tp                   BOOLEAN                  NOT NULL,
    allow_fok                               BOOLEAN                  NOT NULL,
    allow_ioc                               BOOLEAN                  NOT NULL,
    allow_gtc                               BOOLEAN                  NOT NULL,
    allow_gtd                               BOOLEAN                  NOT NULL,
    allow_day_orders                        BOOLEAN                  NOT NULL,
    allow_valid_for_milliseconds            BOOLEAN                  NOT NULL,

    swap_long                               NUMERIC                  NOT NULL,
    swap_short                              NUMERIC                  NOT NULL,
    swap_rate_sunday                        NUMERIC                  NOT NULL,
    swap_rate_monday                        NUMERIC                  NOT NULL,
    swap_rate_tuesday                       NUMERIC                  NOT NULL,
    swap_rate_wednesday                     NUMERIC                  NOT NULL,
    swap_rate_thursday                      NUMERIC                  NOT NULL,
    swap_rate_friday                        NUMERIC                  NOT NULL,
    swap_rate_saturday                      NUMERIC                  NOT NULL,
    swap_year_days                          INTEGER                  NOT NULL,
    swap_mode                               INTEGER                  NOT NULL,

    version                                 BIGINT                   NOT NULL,
    start_date_time                         TIMESTAMP WITH TIME ZONE NOT NULL,
    end_date_time                           TIMESTAMP WITH TIME ZONE NOT NULL,

    symbol_path                             VARCHAR(128)             NOT NULL,
    description                             VARCHAR(64)              NOT NULL,
    exchange                                VARCHAR(64)              NOT NULL,
    country                                 VARCHAR(64)              NOT NULL,
    webpage                                 VARCHAR(256)             NOT NULL,
    underlying_asset                        VARCHAR(32)              NOT NULL,
    isin                                    VARCHAR(16)              NOT NULL,
    cfi                                     VARCHAR(8)               NOT NULL,

    liquidity_margin_rate                   NUMERIC                  NOT NULL,
    bid_markup                              INTEGER                  NOT NULL,
    ask_markup                              INTEGER                  NOT NULL,

    swap_interval                           SMALLINT                 NOT NULL,
    eligible_for_currency_conversion        BOOLEAN                  NOT NULL,

    swap_intraday_starting_time             INTEGER                  NOT NULL,
    price_corridor_threshold                NUMERIC                  NOT NULL,

    industry_tag                            VARCHAR(128)             NOT NULL,
    delay_interval_minutes                  INTEGER                  NOT NULL,
    delay_mode                              INTEGER                  NOT NULL,

    -- the event row this state came from
    source_event_row_id                     BIGINT                   NOT NULL,
    -- when the projection last wrote the row
    time_applied                            TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),

    PRIMARY KEY (sender_id, symbol_id)
);

CREATE VIEW symbols_resync AS
SELECT sender_id,
       max(id) FILTER (WHERE event_type = 2)            AS resync_started_event_row_id,
       max(time_inserted) FILTER (WHERE event_type = 2) AS time_resync_started,
       max(time_inserted) FILTER (WHERE event_type = 3) AS time_resync_finished,
       COALESCE(max(id) FILTER (WHERE event_type = 2), -1) >
       COALESCE(max(id) FILTER (WHERE event_type = 3), -1) AS resync_in_progress
FROM symbol_events
WHERE event_type IN (2, 3)
GROUP BY sender_id;
```

---

## Columns

Every symbol column of [`symbol_events`](symbol_events.md#columns), with the same types, in the same order and with
the same meanings, minus the log's own envelope (`id`, `time_inserted`, `event_type`) and plus:

| Column | Type | Notes |
|--------|------|-------|
| `sender_id`, `symbol_id` | `BIGINT` | `PRIMARY KEY (sender_id, symbol_id)` |
| `source_event_row_id` | `BIGINT` | The `symbol_events.id` this state came from. Traceability, and what a resync's end marker compares against |
| `time_applied` | `TIMESTAMP WITH TIME ZONE` | When the projection last wrote the row |

Every column is `NOT NULL`, and none but `time_applied` has a default. Every column is described on
[`symbol_events`](symbol_events.md#columns), and the enum members — with the margin and profit formulas — under its
[Values](symbol_events.md#values).

There is deliberately **no foreign key** on `source_event_row_id`, for the same reason as on
[`trading_accounts`](trading_accounts.md#columns): an event ages out of the retention window long before the symbol
does.

No indexes beyond the primary key. The table is bounded at the live symbol count — hundreds, low thousands at most —
so a filter the primary key cannot serve, by `symbol_name` or `symbol_path` say, is a scan of a small table.

---

## Projection

[`psql/views/symbols_projection.sql`](../../psql/views/symbols_projection.sql) creates the function `project_symbols()` and this trigger on the log:

```sql
CREATE TRIGGER trg_project_symbols
    AFTER INSERT
    ON symbol_events
    FOR EACH ROW
EXECUTE FUNCTION project_symbols();
```

It behaves exactly as [the trading account projection](trading_accounts.md#projection) does, against the symbol
tables:

| `event_type` | What the trigger does |
|--------------|-----------------------|
| `0` symbol state | Upserts the symbol's row, stamped with the event's `id` |
| `1` deletion | Deletes the symbol's row |
| `2` resync started | **Nothing.** The row's own `id` is the watermark the end marker reads back out of the log |
| `3` resync finished | Deletes that sender's rows stamped older than the newest `2` before it |
| anything else | Nothing — stored in the log, not projected, and no batch fails |

The script is re-runnable and ends with the same reconcile pass, which also applies a resync sweep the trigger missed
because it was absent when the end marker arrived — see
[Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections).

> **Note:** The projection names over a hundred columns in four places — the `INSERT` and the `ON CONFLICT` `SET` in
> the function, then both again in the reconcile. The guard is a test: *Invariant Should copy every field unchanged
> on all four write paths* in `EventLogProjectionIntegrationTests` drives each of the four in turn and compares the
> whole log row against the whole state row as `jsonb`.

---

## symbols_resync

The same answer as [`trading_accounts_resync`](trading_accounts.md#trading_accounts_resync), for the symbol export,
aggregated from the resync markers in [`symbol_events`](symbol_events.md#event-types) — the same columns with the
same meanings. It is a separate view because the two exports are separate pipelines: a sender can be mid-resync on
symbols while its trading account picture is settled, and vice versa.

| | |
|---|---|
| Default name | `symbols_resync` — renamed with `--variables symbols_resync_view=…` |
| Grants | `ro`: `SELECT` · `exporter`: none |

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20261006_02_create_symbols.sql`](../../psql/20261006_02_create_symbols.sql) | incremental | The table |
| [`psql/views/symbols_projection.sql`](../../psql/views/symbols_projection.sql) | views | `project_symbols()`, `trg_project_symbols` and the reconcile pass |
| [`psql/views/symbols_resync.sql`](../../psql/views/symbols_resync.sql) | views | The `symbols_resync` view |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`symbol_events`](symbol_events.md) · [`trades`](trades.md)
