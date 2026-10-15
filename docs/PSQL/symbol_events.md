<div align="center">

# symbol_events

**Every change to a symbol's configuration, as an ordered log — the source `symbols` is derived from**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Event types](#event-types) · [Columns](#columns) · [Values](#values) · [Indexes](#indexes) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `symbol_events` — renamed with `--variables symbol_events_table=…` |
| Written by | The trade server's symbol export pipeline. A resync can be requested on demand with the Admin API's `POST /admin/export/snapshot`, `"entityType": "Symbol"` |
| One row per | Event: a symbol's full configuration, a deletion, or a resync marker |
| Order | `id` — never `time_inserted`, which a whole batch shares |
| Deduplicated by | Nothing — there is no deduplication key |
| Grants | `exporter`: `INSERT`, `SELECT` · `ro`: `SELECT` |
| Derived from it | [`symbols`](symbols.md), by trigger, and the [`symbols_resync`](symbols.md#symbols_resync) view |

The same shape as [`trading_account_events`](trading_account_events.md), for a different entity: a creation or a
modification carries the symbol's full configuration, a deletion carries none of it, and two marker types bracket a
resync. Reducing this log to "which symbols are live and how is each configured" is the job of
[`symbols`](symbols.md) — see [Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections).

The column set, the types and the column order are the schema agreed with the trade server team, and *Schema Should
carry the agreed columns and types in the agreed order* in `SymbolsIntegrationTests` pins them.

---

## Final SQL

The table as the migrator creates it, with the default names substituted.

```sql
CREATE TABLE symbol_events
(
    id                                      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                           TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                               BIGINT                   NOT NULL,

    -- event metadata
    event_type                              SMALLINT                 NOT NULL,
    symbol_id                               BIGINT                   NOT NULL,

    -- quoting and filtering
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

    -- pricing
    max_price_limit                         NUMERIC                  NOT NULL,
    min_price_limit                         NUMERIC                  NOT NULL,
    tick_value                              NUMERIC                  NOT NULL,
    tick_size                               NUMERIC                  NOT NULL,
    lot_size                                NUMERIC                  NOT NULL,

    -- currency decimal places
    base_currency_decimal_places            INTEGER                  NOT NULL,
    profit_currency_decimal_places          INTEGER                  NOT NULL,
    margin_currency_decimal_places          INTEGER                  NOT NULL,

    -- trading modes
    trade_mode                              INTEGER                  NOT NULL,
    margin_calc_mode                        INTEGER                  NOT NULL,
    profit_calc_mode                        INTEGER                  NOT NULL,

    -- order size limits, in lots
    min_order_size_lots                     NUMERIC                  NOT NULL,
    max_order_size_lots                     NUMERIC                  NOT NULL,
    order_size_increment_lots               NUMERIC                  NOT NULL,
    max_position_lots                       NUMERIC                  NOT NULL,

    -- order settings
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

    -- margin amounts
    initial_margin                          NUMERIC                  NOT NULL,
    maintenance_margin                      NUMERIC                  NOT NULL,
    hedged_margin                           NUMERIC                  NOT NULL,

    -- initial margin rates
    initial_buy_margin_rate                 NUMERIC                  NOT NULL,
    initial_sell_margin_rate                NUMERIC                  NOT NULL,
    initial_buy_limit_margin_rate           NUMERIC                  NOT NULL,
    initial_sell_limit_margin_rate          NUMERIC                  NOT NULL,
    initial_buy_stop_margin_rate            NUMERIC                  NOT NULL,
    initial_sell_stop_margin_rate           NUMERIC                  NOT NULL,
    initial_buy_stop_limit_margin_rate      NUMERIC                  NOT NULL,
    initial_sell_stop_limit_margin_rate     NUMERIC                  NOT NULL,

    -- maintenance margin rates
    maintenance_buy_margin_rate             NUMERIC                  NOT NULL,
    maintenance_sell_margin_rate            NUMERIC                  NOT NULL,
    maintenance_buy_limit_margin_rate       NUMERIC                  NOT NULL,
    maintenance_sell_limit_margin_rate      NUMERIC                  NOT NULL,
    maintenance_buy_stop_margin_rate        NUMERIC                  NOT NULL,
    maintenance_sell_stop_margin_rate       NUMERIC                  NOT NULL,
    maintenance_buy_stop_limit_margin_rate  NUMERIC                  NOT NULL,
    maintenance_sell_stop_limit_margin_rate NUMERIC                  NOT NULL,

    -- margin percentage
    percentage                              NUMERIC                  NOT NULL,

    -- identifiers and currencies
    symbol_name                             VARCHAR(32)              NOT NULL DEFAULT '',
    base_currency                           VARCHAR(16)              NOT NULL DEFAULT '',
    profit_currency                         VARCHAR(16)              NOT NULL DEFAULT '',
    margin_currency                         VARCHAR(16)              NOT NULL DEFAULT '',

    -- flags
    margin_use_larger_leg                   BOOLEAN                  NOT NULL,
    recalculate_margin_rate_at_eod          BOOLEAN                  NOT NULL,
    margin_check_on_sl_tp                   BOOLEAN                  NOT NULL,
    allow_fok                               BOOLEAN                  NOT NULL,
    allow_ioc                               BOOLEAN                  NOT NULL,
    allow_gtc                               BOOLEAN                  NOT NULL,
    allow_gtd                               BOOLEAN                  NOT NULL,
    allow_day_orders                        BOOLEAN                  NOT NULL,
    allow_valid_for_milliseconds            BOOLEAN                  NOT NULL,

    -- swaps
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

    -- versioning and validity window
    version                                 BIGINT                   NOT NULL,
    start_date_time                         TIMESTAMP WITH TIME ZONE NOT NULL,
    end_date_time                           TIMESTAMP WITH TIME ZONE NOT NULL,

    -- classification and description
    symbol_path                             VARCHAR(128)             NOT NULL DEFAULT '',
    description                             VARCHAR(64)              NOT NULL DEFAULT '',
    exchange                                VARCHAR(64)              NOT NULL DEFAULT '',
    country                                 VARCHAR(64)              NOT NULL DEFAULT '',
    webpage                                 VARCHAR(256)             NOT NULL DEFAULT '',
    underlying_asset                        VARCHAR(32)              NOT NULL DEFAULT '',
    isin                                    VARCHAR(16)              NOT NULL DEFAULT '',
    cfi                                     VARCHAR(8)               NOT NULL DEFAULT '',

    -- liquidity and markup
    liquidity_margin_rate                   NUMERIC                  NOT NULL,
    bid_markup                              INTEGER                  NOT NULL,
    ask_markup                              INTEGER                  NOT NULL,

    -- swap interval and currency conversion
    swap_interval                           SMALLINT                 NOT NULL,
    eligible_for_currency_conversion        BOOLEAN                  NOT NULL,

    -- intraday swap timing and price corridor
    swap_intraday_starting_time             INTEGER                  NOT NULL,
    price_corridor_threshold                NUMERIC                  NOT NULL,

    -- industry classification and delayed feed
    industry_tag                            VARCHAR(128)             NOT NULL DEFAULT '',
    delay_interval_minutes                  INTEGER                  NOT NULL,
    delay_mode                              INTEGER                  NOT NULL
);

-- a symbol's history
CREATE INDEX idx_symbol_row_symbol_events
    ON symbol_events (sender_id, symbol_id, id);

-- the resync markers only, for the watermark lookup at the end of a resync
CREATE INDEX idx_markers_symbol_events
    ON symbol_events (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
```

---

## Event types

| `event_type` | Meaning | Content columns | `symbol_id` | `version` |
|--------------|---------|-----------------|-------------|-----------|
| `0` | Symbol state — a creation or a modification | The full configuration | The symbol | The symbol's version |
| `1` | Deletion | Zeros | The symbol | `0` |
| `2` | Resync started | Zeros | `0` | `0` |
| `3` | Resync finished | Zeros | `0` | `0` |
| anything else | Unknown to this schema — stored, not projected | — | — | — |

The values are assumed to match the trading account and client logs, where the *Export Setup* guide names them
Upsert, Delete, SnapshotStart and SnapshotEnd; for symbols the trade server team has yet to confirm them. A resync is
also what the Admin API's `POST /admin/export/snapshot` with `"entityType": "Symbol"` starts.

---

## Columns

111 columns: three service columns, the event type, the symbol's id and 106 configuration fields, in the order the
table declares them. Every column is `NOT NULL`, and the exporter writes every one of them on every row — a field
that does not apply carries a type-appropriate zero (`''`, `0`, `false`, the epoch). A delete row carries those zeros
in every content column, `version` included, while `symbol_id` still names the symbol; both marker rows carry them in
`symbol_id` too — see
[Event types](#event-types). That is why a delete row cannot be ordered into a symbol's history by its `version`. Use
`id`.

The configuration fields are the trade server's symbol configuration, as the Admin REST API's `Symbol` resource
carries it; the descriptions are the API's. Where a column is not simply the snake case of its API field, the field
is named.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key, and the write order: ordering by `id` reconstructs the history |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row reached PostgreSQL; defaults to `now()`. A whole batch shares one, so it cannot order events |
| `sender_id` | `BIGINT` | The trade server instance that produced the event |
| `event_type` | `SMALLINT` | See [Event types](#event-types) |
| `symbol_id` | `BIGINT` | API `id`. The symbol. `0` on both markers. The identifier `trades` and the snapshots carry under that name |
| **Quoting and filtering** | | |
| `throttling_price_update_count` | `INTEGER` | API `throttling`. Most price updates published per second, `0`–`1000`; `0` turns throttling off and every update is published |
| `spread_balance` | `INTEGER` | Skews the spread around the mid-price: this many points are added to both the bid and the ask |
| `decimal_places` | `INTEGER` | Price precision, `0`–`12` decimal places |
| `allow_negative_prices` | `BOOLEAN` | Whether negative prices are accepted; when false they are filtered out |
| `allow_realtime_quotes` | `BOOLEAN` | Whether pricing is on. When off there are no prices, though trading may still be allowed |
| `market_depth` | `INTEGER` | Most price bands on each side of the book, `1`–`64` |
| `min_spread_filter` | `INTEGER` | Quotes with a spread below this many points are filtered out. `0` disables |
| `max_spread_filter` | `INTEGER` | Quotes with a spread above this many points are filtered out. `0` disables |
| `soft_filter` | `INTEGER` | When the best bid or ask moves by more than this many points from the previous one, the next `soft_filter_tick_count` quotes are filtered out. `0` disables |
| `soft_filter_tick_count` | `INTEGER` | How many quotes the soft filter drops. `0` disables |
| `hard_filter` | `INTEGER` | Applied after the soft filter has dropped its quotes: a move of more than this many points filters out the next `hard_filter_tick_count`. `0` disables |
| `hard_filter_tick_count` | `INTEGER` | How many quotes the hard filter drops. `0` disables |
| `discard_filter` | `INTEGER` | A quote whose best bid or ask moves by more than this many points is discarded. `0` disables |
| `gap_diff` | `INTEGER` | A move of more than this many points switches gap mode on. `0` disables gap detection |
| `gap_tick_count` | `INTEGER` | Consecutive quotes without a gap needed to switch gap mode off again. `0` disables gap detection |
| **Pricing** | | |
| `max_price_limit` | `NUMERIC` | Highest price allowed |
| `min_price_limit` | `NUMERIC` | Lowest price allowed |
| `tick_value` | `NUMERIC` | Monetary value of a one-tick price change |
| `tick_size` | `NUMERIC` | Smallest price increment allowed |
| `lot_size` | `NUMERIC` | Quantity of the stock, commodity or instrument, or units of the base currency, in one lot |
| `base_currency_decimal_places` | `INTEGER` | Precision of the base currency, `0`–`12` |
| `profit_currency_decimal_places` | `INTEGER` | Precision of the profit currency, `0`–`12` |
| `margin_currency_decimal_places` | `INTEGER` | Precision of the margin currency, `0`–`12` |
| **Trading modes** | | |
| `trade_mode` | `INTEGER` | Which trading the symbol allows — see [Values](#values) |
| `margin_calc_mode` | `INTEGER` | How margin is calculated — see [Values](#values) |
| `profit_calc_mode` | `INTEGER` | How profit is calculated — see [Values](#values) |
| **Order size, in lots** | | |
| `min_order_size_lots` | `NUMERIC` | API `minOrderSize`. Smallest order, in lots, to 8 decimal places |
| `max_order_size_lots` | `NUMERIC` | API `maxOrderSize`. Largest order, in lots |
| `order_size_increment_lots` | `NUMERIC` | API `orderSizeIncrement`. Order size step, in lots |
| `max_position_lots` | `NUMERIC` | API `maxPosition`. Largest total position, in lots. `0` means no limit |
| **Order settings** | | |
| `stops_level` | `INTEGER` | Distance from the market price, in points, within which a stop loss, take profit or pending order cannot be placed. `0` disables the check |
| `freeze_level` | `INTEGER` | Distance from the market price, in points, within which they cannot be modified or deleted. `0` disables the check |
| `allow_market_orders` | `BOOLEAN` | Whether market orders are allowed |
| `allow_limit_orders` | `BOOLEAN` | Whether limit orders are allowed |
| `allow_stop_orders` | `BOOLEAN` | Whether stop orders are allowed |
| `allow_stop_limit_orders` | `BOOLEAN` | Whether stop-limit orders are allowed |
| `allow_stop_loss_orders` | `BOOLEAN` | Whether stop-loss orders are allowed |
| `allow_take_profit_orders` | `BOOLEAN` | Whether take-profit orders are allowed |
| `allow_close_by_orders` | `BOOLEAN` | Whether close-by and close-all orders are allowed |
| `chart_mode` | `INTEGER` | The price chart bars are built from — see [Values](#values) |
| `quotes_timeout_seconds` | `INTEGER` | API `quotesTimeout`. Seconds without a quote after which trading is disabled until the next one. `0` disables the check |
| **Margin** | | |
| `initial_margin` | `NUMERIC` | Initial margin — Futures margin mode only |
| `maintenance_margin` | `NUMERIC` | Maintenance margin — Futures margin mode only. `0` means the initial margin is used |
| `hedged_margin` | `NUMERIC` | Margin multiplier for hedged positions: `0.5` requires half the normal margin |
| `initial_buy_margin_rate` | `NUMERIC` | Initial margin multiplier for buy orders |
| `initial_sell_margin_rate` | `NUMERIC` | Initial margin multiplier for sell orders |
| `initial_buy_limit_margin_rate` | `NUMERIC` | Initial margin multiplier for buy limit orders |
| `initial_sell_limit_margin_rate` | `NUMERIC` | Initial margin multiplier for sell limit orders |
| `initial_buy_stop_margin_rate` | `NUMERIC` | Initial margin multiplier for buy stop orders |
| `initial_sell_stop_margin_rate` | `NUMERIC` | Initial margin multiplier for sell stop orders |
| `initial_buy_stop_limit_margin_rate` | `NUMERIC` | Initial margin multiplier for buy stop-limit orders |
| `initial_sell_stop_limit_margin_rate` | `NUMERIC` | Initial margin multiplier for sell stop-limit orders |
| `maintenance_buy_margin_rate` … `maintenance_sell_stop_limit_margin_rate` | `NUMERIC` | The same eight multipliers, for maintenance margin |
| `percentage` | `NUMERIC` | Additional margin multiplier — the `percentage` of the margin formulas under [Values](#values) |
| **Identity and currencies** | | |
| `symbol_name` | `VARCHAR(32)` | API `name`. The symbol's name, which cannot be changed once created. What `trades` and the snapshots carry as `symbol_name` |
| `base_currency` | `VARCHAR(16)` | Base currency |
| `profit_currency` | `VARCHAR(16)` | Profit currency |
| `margin_currency` | `VARCHAR(16)` | Margin currency |
| **Flags** | | |
| `margin_use_larger_leg` | `BOOLEAN` | Margin on hedged positions: true takes the larger leg, false applies `hedged_margin` |
| `recalculate_margin_rate_at_eod` | `BOOLEAN` | True recalculates the margin conversion rates at the end of every day; false keeps the rate from when the position was opened |
| `margin_check_on_sl_tp` | `BOOLEAN` | True keeps a stop loss or take profit from triggering when the close would lower the margin level and leave too little to maintain the remaining positions |
| `allow_fok` | `BOOLEAN` | Whether Fill or Kill orders are allowed |
| `allow_ioc` | `BOOLEAN` | Whether Immediate or Cancel orders are allowed |
| `allow_gtc` | `BOOLEAN` | Whether Good till Cancelled orders are allowed |
| `allow_gtd` | `BOOLEAN` | Whether Good till Date orders are allowed |
| `allow_day_orders` | `BOOLEAN` | Whether Day orders are allowed |
| `allow_valid_for_milliseconds` | `BOOLEAN` | Whether Valid for Milliseconds orders are allowed |
| **Swaps** | | |
| `swap_long` | `NUMERIC` | Overnight swap on long positions, in the unit `swap_mode` sets |
| `swap_short` | `NUMERIC` | Overnight swap on short positions, in the unit `swap_mode` sets |
| `swap_rate_sunday` … `swap_rate_saturday` | `NUMERIC` | Overnight swap multiplier for each weekday |
| `swap_year_days` | `INTEGER` | Days in a year, for swap calculations |
| `swap_mode` | `INTEGER` | How swaps are calculated — see [Values](#values) |
| **Version and validity** | | |
| `version` | `BIGINT` | The symbol's version. `0` on a deletion and on both markers |
| `start_date_time` | `TIMESTAMP WITH TIME ZONE` | From when the symbol is active |
| `end_date_time` | `TIMESTAMP WITH TIME ZONE` | Until when the symbol is active |
| **Classification** | | |
| `symbol_path` | `VARCHAR(128)` | API `path`. The symbol's path, for example `Forex/` |
| `description` | `VARCHAR(64)` | Description |
| `exchange` | `VARCHAR(64)` | The exchange of an exchange-traded instrument |
| `country` | `VARCHAR(64)` | Country |
| `webpage` | `VARCHAR(256)` | Web page |
| `underlying_asset` | `VARCHAR(32)` | Underlying asset |
| `isin` | `VARCHAR(16)` | International Securities Identification Number |
| `cfi` | `VARCHAR(8)` | CFI code, ISO 10962 |
| **Liquidity and markup** | | |
| `liquidity_margin_rate` | `NUMERIC` | Liquidity margin rate |
| `bid_markup` | `INTEGER` | Markup on the bid. Exported ticks include it |
| `ask_markup` | `INTEGER` | Markup on the ask. Exported ticks include it |
| **Swap interval and currency conversion** | | |
| `swap_interval` | `SMALLINT` | When swaps are calculated — see [Values](#values). The one enum column that is a `SMALLINT` |
| `eligible_for_currency_conversion` | `BOOLEAN` | Whether the symbol takes part in currency conversion |
| **Intraday swaps and price corridor** | | |
| `swap_intraday_starting_time` | `INTEGER` | When intraday swap calculation starts in the day. The API takes it as `HH:MM:SS`; the unit of this integer is to be confirmed |
| `price_corridor_threshold` | `NUMERIC` | Exchange-traded symbols only: the relative price move that triggers margin recomputation — `0.0005` is a 0.05% move either side of the reference price |
| **Industry and delayed feed** | | |
| `industry_tag` | `VARCHAR(128)` | Industry tag |
| `delay_interval_minutes` | `INTEGER` | Market data delay, in whole minutes |
| `delay_mode` | `INTEGER` | Real-time or delayed market data — see [Values](#values) |

Prices, sizes, margins, rates and swaps are unconstrained `NUMERIC`, as in [`trades`](trades.md): a value is stored
exactly as the trade server sent it, with no floating-point rounding on the way in or out.

The text columns carry `DEFAULT ''`, as the agreed schema declares them; `symbols` does not, because the projection
always writes every column and a default there would only mask one it had forgotten. Each `VARCHAR` holds at least
the API's maximum length.

> **Note:** **There is no deduplication key**, as with `trading_account_events`. An event that reaches the
> database twice lands as a second row rather than being rejected. `symbols` absorbs that — a re-applied change
> leaves the same state — but a consumer reading this log directly can see one logical change twice.

> **Note:** The symbol API carries two fields this table has no column for: `quoteSessions` and `tradeSessions`,
> the per-weekday session windows. They are not part of the agreed column set, so neither the log nor `symbols`
> records when a symbol quotes or trades.

---

## Values

Seven columns hold the trade server's `SymbolConfig` enum **ordinal**; the Admin API sends the member's name. The
members and their meanings below are the API's. Their numbers are left out on purpose: the API's order is no proof of
the ordinal — its
client enums omit the `Unspecified` member the export numbers `0`, and its transfer types are listed in a different
order from `trade_type` — so they are to be confirmed against the trade server's enums.

**`trade_mode`** — Disabled, LongOnly, ShortOnly, CloseOnly, Full.

**`margin_calc_mode`** — the margin a position needs:

| Member | Margin |
|--------|--------|
| Forex | lots × lot size ÷ leverage × percentage, in base currency |
| CFD | lots × lot size × open price × percentage, in margin currency |
| CFDLeverage | lots × lot size × open price ÷ leverage × percentage, in margin currency |
| CFDIndex | lots × lot size × open price × tick value ÷ tick size × percentage, in margin currency |
| Futures | lots × initial margin, or lots × maintenance margin × percentage, in margin currency |
| Currency | A single currency held as collateral — not tradable, no margin |
| Collateral | An asset held as collateral — not tradable, no margin; valued at lots × lot size × open price, in margin currency |
| ExchangeStocks | Exchange-traded stocks — margin from the exchange margin subsystem; needs `price_corridor_threshold` |
| ExchangeBonds | Exchange-traded bonds — likewise |

A symbol cannot move between an exchange-traded mode and an OTC one after it is created; within either kind the mode
can change freely.

**`profit_calc_mode`** — the profit of a closed position:

| Member | Profit |
|--------|--------|
| Forex | (close price − open price) × lots × lot size, in profit currency |
| CFD | (close price − open price) × lots × lot size, in profit currency |
| Futures | (close price − open price) × lots × tick value ÷ tick size, in profit currency |
| Currency | A single currency — not tradable, no profit |
| Collateral | An asset held as collateral — not tradable, no profit |

**`chart_mode`** — BidPrice: chart bars are built from the bid.

**`swap_mode`** — the unit of `swap_long` and `swap_short`:

| Member | Swaps are |
|--------|-----------|
| Disabled | Not charged |
| Points | In points |
| BaseCurrency | In base currency, per 1.0 lot |
| ProfitCurrency | In profit currency, per 1.0 lot |
| MarginCurrency | In margin currency, per 1.0 lot |
| AccountCurrency | In account currency, per 1.0 lot |
| InterestCurrent | Interest on the position's notional value at the current market price |
| InterestOpen | Interest on the position's notional value at its open price |

**`swap_interval`** — EndOfDay, the default, or an intraday calculation: IntradayOncePerFullDay,
IntradayTwicePerFullDay, IntradayThreeTimesPerFullDay, IntradayFourTimesPerFullDay.

**`delay_mode`** — Realtime, the default, or Delayed by `delay_interval_minutes`.

### Where a column name differs from the API

| API field | Column |
|-----------|--------|
| `id` | `symbol_id` |
| `name` | `symbol_name` |
| `path` | `symbol_path` |
| `throttling` | `throttling_price_update_count` |
| `minOrderSize`, `maxOrderSize`, `orderSizeIncrement`, `maxPosition` | `min_order_size_lots`, `max_order_size_lots`, `order_size_increment_lots`, `max_position_lots` |
| `quotesTimeout` | `quotes_timeout_seconds` |

Every other field is the snake case of its API name — `bidMarkup` is `bid_markup`, `marginCheckOnSlTp` is
`margin_check_on_sl_tp`.

---

## Indexes

| Index | Definition | Purpose |
|-------|------------|---------|
| `symbol_events_pkey` | `PRIMARY KEY (id)` | |
| `idx_symbol_row_symbol_events` | `(sender_id, symbol_id, id)` | A symbol's history in recorded order; the projection's reconcile pass |
| `idx_markers_symbol_events` | `(sender_id, event_type, id) WHERE event_type IN (2, 3)` | The resync markers only, for the watermark the end marker looks up |

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20261006_01_create_symbol_events.sql`](../../psql/20261006_01_create_symbol_events.sql) | incremental | The table and both indexes |
| [`psql/views/symbols_projection.sql`](../../psql/views/symbols_projection.sql) | views | The `AFTER INSERT` trigger on this table — see [`symbols`](symbols.md#projection) |
| [`psql/views/symbols_resync.sql`](../../psql/views/symbols_resync.sql) | views | The resync view over the markers |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`symbols`](symbols.md) · [`ticks`](../ClickHouse/ticks.md)
