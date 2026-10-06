<div align="center">

# Schema Reference

**The tables and views the export uses, on both engines, column by column**

[Overview](#overview) · [trades](#trades) · [daily_statements_export](#daily_statements_export) · [order snapshots](#daily_statements_order_snapshots) · [position snapshots](#daily_statements_position_snapshots) · [export_cursor](#export_cursor) · [trading_account_events](#trading_account_events) · [trading_accounts](#trading_accounts) · [symbol_events](#symbol_events) · [symbols](#symbols) · [Views](#views) · [ticks](#ticks) · [schema_versions](#schema_versions) · [Deduplication](#deduplication) · [Caveats](#caveats)

</div>

---

## Overview

The trade server exports each entity to exactly one engine, so the two engines hold different tables. Every one
of them is created by the migrator, in the database named by the connection string:

| Table | Default name | Engine | Written by | Purpose |
|-------|--------------|--------|------------|---------|
| Trades | `trades` | PostgreSQL | trade server | Every exported trade record, append-only |
| Daily statements | `daily_statements_export` | PostgreSQL | trade server | One end-of-day row per account |
| Order snapshots | `daily_statements_order_snapshots` | PostgreSQL | trade server | The account's working orders at the statement time |
| Position snapshots | `daily_statements_position_snapshots` | PostgreSQL | trade server | The account's open positions at the statement time |
| Trading account events | `trading_account_events` | PostgreSQL | trade server | Every trading account change, as an ordered log |
| Trading accounts | `trading_accounts` | PostgreSQL | **migrator, by trigger** | The current state of every live trading account |
| Symbol events | `symbol_events` | PostgreSQL | trade server | Every symbol configuration change, as an ordered log |
| Symbols | `symbols` | PostgreSQL | **migrator, by trigger** | The current configuration of every live symbol |
| Export cursor | `export_cursor` | PostgreSQL | trade server | How far each PostgreSQL export has progressed |
| Ticks | `ticks` | ClickHouse | trade server | The top of the order book of every symbol, one row per change |
| Journal | `schema_versions` | both | migrator | Which scripts have been applied |

…and four views, on PostgreSQL:

| View | Default name | Purpose |
|------|--------------|---------|
| Trades history | `trades_history` | The executed and cancelled trades, without the accounting records |
| Cash transfers | `cash_transfers` | The cash movements, over the columns a transfer actually populates |
| Trading accounts resync | `trading_accounts_resync` | Whether a resync is in progress, per sender, read from the log's markers |
| Symbols resync | `symbols_resync` | The same answer for the symbol export, read from `symbol_events`' markers |

The eight names the trade server writes to are configurable — they mirror its export configuration and are
templated as `##trades_table##`, `##daily_statements_table##`, `##order_snapshots_table##`,
`##position_snapshots_table##`, `##trading_account_events_table##`, `##symbol_events_table##`,
`##cursor_table##` and `##ticks_table##`. The journal name comes from `--journal-table`. `trading_accounts` and
`symbols` are templated too, as `##trading_accounts_table##` and `##symbols_table##`, even though the trade
server never writes to either — a deployment may already own an object under one of those names.

The three daily statement tables are produced by one export: when a statement is generated the trade server
publishes the statement row, a snapshot of every **working** order and a snapshot of every open position, all
carrying the same scheduled end-of-day `time_statement`. That shared timestamp is what ties the three together —
there are no foreign keys.

```
statement run at time_statement
├── daily_statements_export             one row per account
├── daily_statements_order_snapshots    one row per working order
└── daily_statements_position_snapshots one row per open position
```

Each has its own pipeline in the trade server's `export_config.json`, so a deployment can export the statement
without the snapshots. Every PostgreSQL pipeline shares the one `export_cursor` table, which holds a row per
`(sender_id, table_name)`.

> **Important:** **The engines are not mirrors of each other.** The *Export Setup* guide sends trades, daily
> statements, their snapshots, the trading account events, the symbol events and the cursor to PostgreSQL only,
> and ticks to ClickHouse only; a pipeline that pairs an entity with the other engine is rejected by the trade
> server. So `psql/` creates those seven tables, the `trading_accounts` and `symbols` projections and the four
> views, `clickhouse/` creates the ticks table, and both create the journal.

Every definition matches the *Export Setup* guide.

---

## trades

PostgreSQL. Append-only. Delivery is at-least-once, so the same trade can arrive twice after a restart — see
[Deduplication](#deduplication).

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Server-generated |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | Defaults to `now()` — when the row landed, not when the trade happened |
| `sender_id` | `BIGINT` | Identifies the writing trade server |
| `group_name` | `VARCHAR(128)` | |
| `symbol_name` | `VARCHAR(32)` | |
| `trade_id` | `BIGINT` | Unique per `sender_id` |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | When the trade was created |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | |
| `trading_account_id` | `BIGINT` | |
| `group_id` | `BIGINT` | |
| `trade_type` | `SMALLINT` | Trade / Balance / Credit / Fee and the other accounting types |
| `executed_price` | `NUMERIC` | |
| `volume` | `NUMERIC` | |
| `order_id` | `BIGINT` | |
| `position_id` | `BIGINT` | |
| `profit` | `NUMERIC` | |
| `commission` | `NUMERIC` | |
| `swaps` | `NUMERIC` | |
| `fee` | `NUMERIC` | |
| `position_price` | `NUMERIC` | |
| `profit_conversion_rate` | `NUMERIC` | |
| `contract_size` | `NUMERIC` | |
| `notional_value_in_original_currency` | `NUMERIC` | |
| `notional_value_in_account_currency` | `NUMERIC` | |
| `tick_value` | `NUMERIC` | |
| `tick_size` | `NUMERIC` | |
| `market_bid` | `NUMERIC` | |
| `market_ask` | `NUMERIC` | |
| `symbol_id` | `BIGINT` | |
| `group_markup` | `NUMERIC` | |
| `side` | `SMALLINT` | |
| `entry_type` | `SMALLINT` | |
| `reason` | `SMALLINT` | |
| `price_decimal_places` | `SMALLINT` | |
| `account_currency_decimal_places` | `SMALLINT` | |
| `comment` | `VARCHAR(64)` | Defaults to `''` |
| `agent_commission` | `NUMERIC` | |
| `notional_value_in_usd` | `NUMERIC` | |

Every column is `NOT NULL`.

**Indexes**

| Index | Definition | Purpose |
|-------|------------|---------|
| `idx_sender_trade_<trades_table>` | `UNIQUE (sender_id, trade_id)` | Deduplicates at-least-once delivery |
| `idx_time_created_<trades_table>` | `(time_created)` | Time-range queries |
| `idx_account_time_<trades_table>` | `(trading_account_id, time_created)` | An account's trades over a period - the main reporting path, and `trades_history` |
| `idx_transfers_account_time_<trades_table>` | `(trading_account_id, time_created)` `WHERE trade_type NOT IN (0, 1, 14, 15)` | The same query against `cash_transfers`; partial, so it holds only the accounting records |

What the index covers comes **before** the table name, because PostgreSQL truncates identifiers to 63 bytes: a
long configured table name would otherwise leave both indexes with the same truncated name, and the second
`CREATE INDEX IF NOT EXISTS` would quietly do nothing.

---

## daily_statements_export

PostgreSQL. One row per account per statement run. Append-only, at-least-once, same as trades.

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Server-generated |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | Defaults to `now()` — when the row landed |
| `sender_id` | `BIGINT` | Identifies the writing trade server |
| `time_statement` | `TIMESTAMP WITH TIME ZONE` | The scheduled end-of-day time. Shared with the snapshots of the same run |
| `group_name` | `VARCHAR(128)` | |
| `trading_account_id` | `BIGINT` | |
| `group_id` | `BIGINT` | |
| `statement_client_name` | `VARCHAR(160)` | Account owner |
| `account_currency` | `VARCHAR(16)` | `USD`, `EUR`, … |
| `email` | `VARCHAR(64)` | |
| `current_balance` | `NUMERIC` | |
| `prev_day_balance` | `NUMERIC` | |
| `current_credit` | `NUMERIC` | |
| `prev_day_credit` | `NUMERIC` | |
| `current_floating_profit` | `NUMERIC` | Unrealised, on open positions |
| `lifetime_realized_profit` | `NUMERIC` | Since the account was created |
| `current_equity` | `NUMERIC` | Balance plus floating profit |
| `prev_day_equity` | `NUMERIC` | |
| `current_margin` | `NUMERIC` | |
| `current_collateral` | `NUMERIC` | |
| `prev_day_collateral` | `NUMERIC` | |
| `open_positions_swaps` | `NUMERIC` | |
| `closed_positions_swaps` | `NUMERIC` | For the statement period |
| `commissions` | `NUMERIC` | |
| `agent_commissions` | `NUMERIC` | |
| `fees` | `NUMERIC` | |
| `interest` | `NUMERIC` | |
| `deposits` | `NUMERIC` | |
| `withdrawals` | `NUMERIC` | |
| `credit_transactions` | `NUMERIC` | |
| `adjustment_transactions` | `NUMERIC` | |
| `balance_bonus_transactions` | `NUMERIC` | |
| `credit_bonus_transactions` | `NUMERIC` | |
| `dividend_transactions` | `NUMERIC` | |
| `tax` | `NUMERIC` | |
| `stopout_compensations` | `NUMERIC` | |
| `stopout_credit_adjustments` | `NUMERIC` | |
| `volume_in_usd` | `NUMERIC` | |
| `volume_in_account_currency` | `NUMERIC` | |
| `realized_profit` | `NUMERIC` | For the statement period |
| `account_currency_decimal_places` | `SMALLINT` | Usually 2 |

Every column is `NOT NULL`.

**Indexes**

| Index | Definition | Purpose |
|-------|------------|---------|
| `idx_sender_stmt_account_<daily_statements_table>` | `UNIQUE (sender_id, time_statement, trading_account_id)` | Deduplicates at-least-once delivery |
| `idx_account_stmt_<daily_statements_table>` | `(trading_account_id, time_statement)` | An account's statement history. The unique index carries `trading_account_id` third, so it cannot serve a filter on the account alone |

---

## daily_statements_order_snapshots

PostgreSQL. One row per **working** order of the account, as it stood at `time_statement`. A filled, cancelled
or expired order is not part of a statement.

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Server-generated |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | Defaults to `now()` |
| `sender_id` | `BIGINT` | Identifies the writing trade server |
| `time_statement` | `TIMESTAMP WITH TIME ZONE` | Same value as on the statement row |
| `order_id` | `BIGINT` | Unique per `sender_id` and `time_statement` |
| `order_history_record_id` | `BIGINT` | Tracks order modifications |
| `trading_account_id` | `BIGINT` | |
| `group_id` | `BIGINT` | |
| `group_name` | `VARCHAR(128)` | |
| `symbol_name` | `VARCHAR(32)` | |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | |
| `classification_id` | `BIGINT` | |
| `symbol_id` | `BIGINT` | |
| `parent_order_id` | `BIGINT` | 0 when standalone |
| `oco_order_id` | `BIGINT` | 0 when not part of an OCO pair |
| `stop_loss_order_id` | `BIGINT` | 0 when none |
| `take_profit_order_id` | `BIGINT` | 0 when none |
| `parent_position_id` | `BIGINT` | 0 for a new position |
| `position_id` | `BIGINT` | 0 when the order opened no position |
| `position_by_id` | `BIGINT` | 0 when not applicable |
| `liquidity_connection_id` | `BIGINT` | 0 for a house order |
| `volume` | `NUMERIC` | In lots |
| `remaining_volume` | `NUMERIC` | Unfilled |
| `avg_fill_price` | `NUMERIC` **nullable** | `NULL` when unfilled |
| `stop_price` | `NUMERIC` **nullable** | `NULL` when not a stop order |
| `limit_price` | `NUMERIC` **nullable** | `NULL` when not applicable |
| `margin_conversion_rate` | `NUMERIC` | |
| `lot_size` | `NUMERIC` | Contract multiplier |
| `order_type` | `SMALLINT` | Market / Stop / Limit / Stop-Limit / Close By |
| `order_expiry` | `SMALLINT` | FOK / IOC / GTC / GTD / Day / Milliseconds |
| `order_side` | `SMALLINT` | 0 Buy, 1 Sell |
| `order_status` | `SMALLINT` | Only working orders are exported |
| `group_markup` | `NUMERIC` | |
| `price_decimal_places` | `SMALLINT` | |
| `account_currency_decimal_places` | `SMALLINT` | |
| `expiration_time` | `TIMESTAMP WITH TIME ZONE` **nullable** | `NULL` unless the order is Good Till Date |
| `reason` | `SMALLINT` | Same value set as on a trade |
| `comment` | `VARCHAR(64)` | |

**Indexes**

| Index | Definition | Purpose |
|-------|------------|---------|
| `idx_sender_stmt_order_<order_snapshots_table>` | `UNIQUE (sender_id, time_statement, order_id)` | Deduplicates at-least-once delivery |
| `idx_account_stmt_<order_snapshots_table>` | `(trading_account_id, time_statement)` | The working orders behind one account's statement. `trading_account_id` is not in the unique index at all |

---

## daily_statements_position_snapshots

PostgreSQL. One row per open position of the account, as it stood at `time_statement`.

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. Server-generated |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | Defaults to `now()` |
| `sender_id` | `BIGINT` | Identifies the writing trade server |
| `time_statement` | `TIMESTAMP WITH TIME ZONE` | Same value as on the statement row |
| `position_id` | `BIGINT` | Unique per `sender_id` and `time_statement` |
| `trading_account_id` | `BIGINT` | |
| `group_id` | `BIGINT` | |
| `group_name` | `VARCHAR(128)` | |
| `symbol_name` | `VARCHAR(32)` | |
| `symbol_id` | `BIGINT` | |
| `side` | `SMALLINT` | 0 Buy (long), 1 Sell (short) |
| `volume` | `NUMERIC` | In lots |
| `avg_open_price` | `NUMERIC` | |
| `market_price` | `NUMERIC` | At snapshot time |
| `profit` | `NUMERIC` | Unrealised, in account currency |
| `swaps` | `NUMERIC` | |
| `commission` | `NUMERIC` | |
| `fee` | `NUMERIC` | |
| `margin` | `NUMERIC` | |
| `stop_loss` | `NUMERIC` **nullable** | `NULL` when not set |
| `stop_loss_order_id` | `BIGINT` | 0 when none |
| `take_profit` | `NUMERIC` **nullable** | `NULL` when not set |
| `take_profit_order_id` | `BIGINT` | 0 when none |
| `stopout_order_id` | `BIGINT` | 0 when no stop-out happened |
| `profit_conversion_rate` | `NUMERIC` | |
| `margin_conversion_rate` | `NUMERIC` | |
| `lot_size` | `NUMERIC` | Contract multiplier |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | When the position was opened |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | |
| `price_decimal_places` | `SMALLINT` | |
| `account_currency_decimal_places` | `SMALLINT` | |
| `intraday_swap_day` | `NUMERIC` | Accrued today |
| `intraday_swap_month` | `NUMERIC` | Accrued this month |
| `created_by_order_id` | `BIGINT` | |
| `updated_by_order_id` | `BIGINT` | Most recent order that modified the position |

**Indexes**

| Index | Definition | Purpose |
|-------|------------|---------|
| `idx_sender_stmt_position_<position_snapshots_table>` | `UNIQUE (sender_id, time_statement, position_id)` | Deduplicates at-least-once delivery |
| `idx_account_stmt_<position_snapshots_table>` | `(trading_account_id, time_statement)` | The open positions behind one account's statement. `trading_account_id` is not in the unique index at all |

---

## export_cursor

PostgreSQL. How many records the trade server has already committed for a given table — one row per
`(sender_id, table_name)`, so every PostgreSQL export pipeline of every server instance shares this one table.
It — not the trade server's local `state.bin` — is the authoritative position; the local file is only a
shutdown-time cache.

| Column | Type | Notes |
|--------|------|-------|
| `sender_id` | `BIGINT` | Part of the primary key |
| `table_name` | `TEXT` | Part of the primary key |
| `last_written_position` | `BIGINT` | The committed position |

`PRIMARY KEY (sender_id, table_name)`.

The cursor row and the batch of records are written **in the same transaction**, which is what makes the cursor
trustworthy: it can never claim more than was actually stored.

There is no cursor on ClickHouse: a ClickHouse pipeline keeps its position on disk only, and after a crash it
starts again from position 0. The *Export Setup* guide describes the consequences.

---

## trading_account_events

Every change to a trading account, as an ordered log. A creation or a modification carries the account's full
state; a deletion carries none of it; and two marker types bracket a resync and are not about an account at all.

Reducing this log to "which accounts are live" is the job of [`trading_accounts`](#trading_accounts), which a
trigger maintains from it — see [Trading account projection](#trading-account-projection).

### PostgreSQL

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. The recorded order, and the only column that orders events |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row reached PostgreSQL. Transaction start time, so a whole batch shares one — it cannot order events |
| `sender_id` | `BIGINT` | The trade server instance that produced the event |
| `event_type` | `SMALLINT` | `0` account state, `1` deletion, `2` resync started, `3` resync finished |
| `trading_account_id` | `BIGINT` | `0` on both marker rows |
| `version` | `BIGINT` | The account's version as of this event. `0` on a deletion and on both markers |
| `group_id` | `BIGINT` | |
| `client_id` | `BIGINT` | |
| `introducer` | `BIGINT` | `0` means no introducing broker |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | |
| `time_password_last_changed` | `TIMESTAMP WITH TIME ZONE` | The epoch means never changed |
| `leverage` | `INTEGER` | |
| `enabled` | `BOOLEAN` | |
| `allow_trading` | `BOOLEAN` | |
| `external_account_id` | `VARCHAR(64)` | `''` means none |
| `comment` | `VARCHAR(256)` | |

Every column is `NOT NULL`, and the exporter writes every one of them on every row — a field that does not
apply carries a type-appropriate zero rather than relying on a column default. A delete row and both marker
rows carry those zeros throughout, `version` and `trading_account_id` included, which is why a delete row cannot
be ordered into an account's history by its `version`. Use `id`.

Indexes: `(sender_id, trading_account_id, id)` for an account's history, and a partial
`(sender_id, event_type, id) WHERE event_type IN (2, 3)` for the resync markers.

> **Note:** **There is no deduplication key**, unlike every other exported table here. Delivery is
> at-least-once, so a redelivered event lands as a second row rather than being rejected. `trading_accounts`
> absorbs that — a re-applied change leaves the same state — but a consumer reading this log directly can see
> one logical change twice.

> **Warning:** The account password is deliberately absent and must stay absent. It is never present in an API
> response, nothing here has a use for it, and leaving it out of the schema is what keeps it out of the
> database — the `ro` role can read every table in here.

---

## trading_accounts

The current state of every live trading account: one row per `(sender_id, trading_account_id)`, so "which
accounts are live and how is each configured" is a single read.

**Not written by the trade server.** It is derived from `trading_account_events` by a trigger, inside the same
transaction as the event that caused the change — so it cannot lag behind the log, and a batch that fails to
commit leaves no trace in it.

### PostgreSQL

Every account column of `trading_account_events`, with the same types and the same meanings, minus the log's
own envelope (`id`, `time_inserted`, `event_type`) and plus:

| Column | Type | Notes |
|--------|------|-------|
| `sender_id`, `trading_account_id` | `BIGINT` | `PRIMARY KEY (sender_id, trading_account_id)` |
| `source_event_row_id` | `BIGINT` | The `trading_account_events.id` this state came from. Traceability, and what a resync's end marker compares against |
| `time_applied` | `TIMESTAMP WITH TIME ZONE` | When the projection last wrote the row, as against `time_modified`, when the account last changed |

There is deliberately **no foreign key** on `source_event_row_id`: an event ages out of the retention window
long before the account does, and a foreign key would make that purge either fail or cascade into here — which
is the one thing this table exists not to do.

No indexes beyond the primary key. The table is bounded at roughly the live account count, so a filter the
primary key cannot serve is a scan of a small table rather than of a growing one.

---

## Trading account projection

`psql/views/trading_accounts_projection.sql` creates the function `project_trading_accounts()` and the trigger
`trg_project_trading_accounts`, an `AFTER INSERT … FOR EACH ROW` trigger on `trading_account_events`.

| `event_type` | What the trigger does |
|--------------|-----------------------|
| `0` account state | Upserts the account's row, stamped with the event's `id` |
| `1` deletion | Deletes the account's row |
| `2` resync started | **Nothing.** The row's own `id` is the watermark the end marker reads back out of the log |
| `3` resync finished | Deletes that sender's rows stamped older than the newest `2` before it |
| anything else | Nothing — stored in the log, not projected, and no batch fails |

Ordering is by arrival, never by `version`: trading account ids are reused after deletion, so a reused id
restarts its version sequence, and deletions and markers carry `version 0` anyway.

A resync sweeps at the **end** marker rather than the start, so the table stays complete and readable for the
whole resync — which matters because a resync runs on every trade server startup that has it enabled.

The script is re-runnable and ends with a reconcile pass that replays the newest retained event per account, so
`--force` repairs a dropped trigger, and corrects rows an earlier, buggy version of the projection wrote. It
cannot recover what the retention window has
dropped; the authoritative repair for that is a resync from the trade server.

---

## symbol_events

Every change to a symbol's configuration, as an ordered log. A creation or a modification carries the symbol's
full configuration; a deletion carries none of it; and two marker types bracket a resync and are not about a
symbol at all. The same shape as [`trading_account_events`](#trading_account_events), for a different entity.

Reducing this log to "which symbols are live and how is each configured" is the job of [`symbols`](#symbols),
which a trigger maintains from it — see [Symbol projection](#symbol-projection).

The column set, the types and the **column order** are the schema agreed with the trade server team, so the
exporter lines up whether its `COPY` names its columns or relies on their position.

### PostgreSQL

111 columns: a four-column envelope, the symbol's id, and 106 configuration fields, in the groups the table
declares them in.

| Column | Type | Notes |
|--------|------|-------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key. The recorded order, and the only column that orders events |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row reached PostgreSQL. Transaction start time, so a whole batch shares one — it cannot order events |
| `sender_id` | `BIGINT` | The trade server instance that produced the event |
| `event_type` | `SMALLINT` | `0` symbol state, `1` deletion, `2` resync started, `3` resync finished |
| `symbol_id` | `BIGINT` | `0` on both marker rows. The identifier `trades` and the order and position snapshots carry under that name |
| `throttling_price_update_count` | `INTEGER` | API `throttling` |
| `spread_balance`, `decimal_places`, `market_depth` | `INTEGER` | |
| `allow_negative_prices`, `allow_realtime_quotes` | `BOOLEAN` | |
| `min_spread_filter`, `max_spread_filter`, `soft_filter`, `soft_filter_tick_count`, `hard_filter`, `hard_filter_tick_count`, `discard_filter`, `gap_diff`, `gap_tick_count` | `INTEGER` | The quote filters |
| `max_price_limit`, `min_price_limit`, `tick_value`, `tick_size`, `lot_size` | `NUMERIC` | |
| `base_currency_decimal_places`, `profit_currency_decimal_places`, `margin_currency_decimal_places` | `INTEGER` | |
| `trade_mode`, `margin_calc_mode`, `profit_calc_mode` | `INTEGER` | Enum ordinals — see [Symbol enum columns](#symbol-enum-columns) |
| `min_order_size_lots`, `max_order_size_lots`, `order_size_increment_lots`, `max_position_lots` | `NUMERIC` | In lots. API `minOrderSize`, `maxOrderSize`, `orderSizeIncrement`, `maxPosition` |
| `stops_level`, `freeze_level` | `INTEGER` | |
| `allow_market_orders`, `allow_limit_orders`, `allow_stop_orders`, `allow_stop_limit_orders`, `allow_stop_loss_orders`, `allow_take_profit_orders`, `allow_close_by_orders` | `BOOLEAN` | The order types the symbol accepts |
| `chart_mode` | `INTEGER` | Enum ordinal |
| `quotes_timeout_seconds` | `INTEGER` | API `quotesTimeout` |
| `initial_margin`, `maintenance_margin`, `hedged_margin` | `NUMERIC` | |
| `initial_buy_margin_rate` … `initial_sell_stop_limit_margin_rate` | `NUMERIC` | Eight rates: buy and sell, each for market, limit, stop and stop-limit |
| `maintenance_buy_margin_rate` … `maintenance_sell_stop_limit_margin_rate` | `NUMERIC` | The same eight, for maintenance margin |
| `percentage` | `NUMERIC` | |
| `symbol_name` | `VARCHAR(32)` | API `name`. The name `trades` and the snapshots carry as `symbol_name` |
| `base_currency`, `profit_currency`, `margin_currency` | `VARCHAR(16)` | |
| `margin_use_larger_leg`, `recalculate_margin_rate_at_eod`, `margin_check_on_sl_tp` | `BOOLEAN` | |
| `allow_fok`, `allow_ioc`, `allow_gtc`, `allow_gtd`, `allow_day_orders`, `allow_valid_for_milliseconds` | `BOOLEAN` | The time-in-force values the symbol accepts |
| `swap_long`, `swap_short` | `NUMERIC` | |
| `swap_rate_sunday` … `swap_rate_saturday` | `NUMERIC` | One per weekday |
| `swap_year_days` | `INTEGER` | |
| `swap_mode` | `INTEGER` | Enum ordinal |
| `version` | `BIGINT` | The symbol's version as of this event. `0` on a deletion and on both markers |
| `start_date_time`, `end_date_time` | `TIMESTAMP WITH TIME ZONE` | |
| `symbol_path` | `VARCHAR(128)` | API `path`, for example `Forex/` |
| `description`, `exchange`, `country` | `VARCHAR(64)` | |
| `webpage` | `VARCHAR(256)` | |
| `underlying_asset` | `VARCHAR(32)` | |
| `isin` | `VARCHAR(16)` | |
| `cfi` | `VARCHAR(8)` | |
| `liquidity_margin_rate` | `NUMERIC` | |
| `bid_markup`, `ask_markup` | `INTEGER` | |
| `swap_interval` | `SMALLINT` | Enum ordinal — the one enum column that is a `SMALLINT` |
| `eligible_for_currency_conversion` | `BOOLEAN` | |
| `swap_intraday_starting_time` | `INTEGER` | |
| `price_corridor_threshold` | `NUMERIC` | |
| `industry_tag` | `VARCHAR(128)` | |
| `delay_interval_minutes` | `INTEGER` | |
| `delay_mode` | `INTEGER` | Enum ordinal |

Every column is `NOT NULL`, and the exporter writes every one of them on every row — a field that does not
apply carries a type-appropriate zero (`''`, `0`, `false`, the epoch). A delete row and both marker rows carry
those zeros throughout, `version` and `symbol_id` included, which is why a delete row cannot be ordered into a
symbol's history by its `version`. Use `id`.

Prices, sizes, margins, rates and swaps are unconstrained `NUMERIC`, as in [`trades`](#trades): a value is
stored exactly as the trade server sent it, with no floating-point rounding on the way in or out.

The text columns carry `DEFAULT ''`, as the agreed schema declares them; `symbols` does not, because the
projection always writes every column and a default there would only mask one it had forgotten.

Indexes: `(sender_id, symbol_id, id)` for a symbol's history, and a partial
`(sender_id, event_type, id) WHERE event_type IN (2, 3)` for the resync markers.

#### Symbol enum columns

Seven columns hold the trade server's `SymbolConfig` enum **ordinal**, not its name — the same convention as
`trade_type`, `side`, `entry_type` and `reason` in [`trades`](#trades). The symbol API sends the name instead;
the examples are from its `USDJPY` sample.

| Column | API field | Example in the API |
|--------|-----------|--------------------|
| `trade_mode` | `tradeMode` | `Full` |
| `margin_calc_mode` | `marginCalcMode` | `Forex` |
| `profit_calc_mode` | `profitCalcMode` | `Forex` |
| `chart_mode` | `chartMode` | `BidPrice` |
| `swap_mode` | `swapMode` | `Points` |
| `swap_interval` | `swapInterval` | `EndOfDay` |
| `delay_mode` | — | — |

> **Note:** The trade server's enum definitions are the authority on which ordinal each name maps to, and their
> member lists are not reproduced here. Nothing validates the range, so a member added later cannot fail the
> export.

#### Where a column name differs from the API

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

> **Note:** **There is no deduplication key**, as with `trading_account_events`. Delivery is at-least-once, so a
> redelivered event lands as a second row rather than being rejected. `symbols` absorbs that — a re-applied
> change leaves the same state — but a consumer reading this log directly can see one logical change twice.

> **Note:** The symbol API carries two fields this table has no column for: `quoteSessions` and
> `tradeSessions`, the per-weekday session windows. They are not part of the agreed column set, so neither the
> log nor `symbols` records when a symbol quotes or trades.

### ClickHouse

Out of scope. `ticks` carries `symbol_id` and `short_symbol_name`, but ClickHouse has no trigger mechanism, so a
deployment that ever wants the symbol configuration beside the ticks needs its own current-state mechanism and
its own grants.

---

## symbols

The current configuration of every live symbol: one row per `(sender_id, symbol_id)`, so "which symbols are
live and how is each configured" is a single read.

**Not written by the trade server.** It is derived from `symbol_events` by a trigger, inside the same
transaction as the event that caused the change — so it cannot lag behind the log, and a batch that fails to
commit leaves no trace in it.

`symbol_id` is the identifier [`trades`](#trades), [order snapshots](#daily_statements_order_snapshots) and
[position snapshots](#daily_statements_position_snapshots) carry under that name, so a trade joins to its symbol
directly — on `sender_id` **and** `symbol_id`. Both halves are required: each trade server instance numbers its
own symbols, so symbol `25` on one instance and symbol `25` on another can be two different instruments.

```sql
SELECT t.sender_id, t.trade_id, t.symbol_name, s.profit_currency, s.margin_calc_mode
FROM trades t
         LEFT JOIN symbols s ON s.sender_id = t.sender_id AND s.symbol_id = t.symbol_id;
```

`LEFT JOIN`, because a trade outlives its symbol: once a symbol is deleted it has no row here.

> **Important:** This is the symbol's **current** configuration, not the one in force when a trade executed. A
> lot size, tick value or margin rate changed since then shows as it is now. The trade row already carries the
> values that mattered at execution — `contract_size`, `tick_value`, `tick_size`, `price_decimal_places` — and
> those are the ones to use for that trade. For the configuration at an earlier time, read `symbol_events`,
> within its retention.

Join on the id, not on `symbol_name`. There is deliberately **no unique key** on `(sender_id, symbol_name)`:
between a resync's start and end markers, a symbol deleted while the sender was down and a new one created
under its name both hold a row, and a unique index would turn the re-announcement of the new one into a
constraint violation that stalls the export.

### PostgreSQL

Every symbol column of `symbol_events`, with the same types, in the same order and with the same meanings, minus
the log's own envelope (`id`, `time_inserted`, `event_type`) and plus:

| Column | Type | Notes |
|--------|------|-------|
| `sender_id`, `symbol_id` | `BIGINT` | `PRIMARY KEY (sender_id, symbol_id)` |
| `source_event_row_id` | `BIGINT` | The `symbol_events.id` this state came from. Traceability, and what a resync's end marker compares against |
| `time_applied` | `TIMESTAMP WITH TIME ZONE` | When the projection last wrote the row |

There is deliberately **no foreign key** on `source_event_row_id`, for the same reason as `trading_accounts`: an
event ages out of the retention window long before the symbol does.

No indexes beyond the primary key. The table is bounded at the live symbol count — hundreds, low thousands at
most — so a filter the primary key cannot serve, by `symbol_name` or `symbol_path` say, is a scan of a small
table.

### ClickHouse

Out of scope, as above.

---

## Symbol projection

`psql/views/symbols_projection.sql` creates the function `project_symbols()` and the trigger
`trg_project_symbols`, an `AFTER INSERT … FOR EACH ROW` trigger on `symbol_events`. It behaves exactly as
[the trading account projection](#trading-account-projection) does, against the symbol tables:

| `event_type` | What the trigger does |
|--------------|-----------------------|
| `0` symbol state | Upserts the symbol's row, stamped with the event's `id` |
| `1` deletion | Deletes the symbol's row |
| `2` resync started | **Nothing.** The row's own `id` is the watermark the end marker reads back out of the log |
| `3` resync finished | Deletes that sender's rows stamped older than the newest `2` before it |
| anything else | Nothing — stored in the log, not projected, and no batch fails |

Ordering is by arrival, never by `version`, and a resync sweeps at the **end** marker rather than the start —
the reasoning is identical to the trading account projection's and is not repeated here.

The script is re-runnable and ends with the same reconcile pass, which also applies a resync sweep the trigger
missed because it was absent when the end marker arrived. So `--force` repairs a dropped trigger and corrects
rows an earlier, buggy version of the projection wrote.

> **Note:** The projection names over a hundred columns in four places — the `INSERT` and the `ON CONFLICT`
> `SET` in the function, then both again in the reconcile. Reading those lists against each other is not a
> realistic review, so the guard is a test: *Invariant Should copy every field from the event unchanged on all
> four write paths* in `SymbolsProjectionIntegrationTests` drives each of the four in turn and compares the
> whole log row against the whole state row as `jsonb`, so it fails on any column one of them crosses, drops or
> leaves behind.

---

## Views

Four views, on PostgreSQL. Two are over `trades`, and exist because the trades table holds two unrelated kinds
of record — an executed trade and a cash movement on the account — and a reader almost always wants one or the
other.

| View | Default name | Rows | Columns |
|------|--------------|------|---------|
| Trades history | `trades_history` | `trade_type` in (0, 1, 14, 15) | every `trades` column except the identity |
| Cash transfers | `cash_transfers` | every other `trade_type` | the 22 the transfer pipeline populates, plus 2 |

Both names are templated — `##trades_history_view##` and `##cash_transfers_view##` — so a deployment that
already owns an object under one of them moves ours aside with `--variables`.

The two filters are complements, so **the views partition the table**: every row is visible through exactly one
of them, and a trade type the trade server gains later surfaces in `cash_transfers` rather than in neither.

The other two are over the event logs rather than over `trades` — see
[trading_accounts_resync](#trading_accounts_resync) and [symbols_resync](#symbols_resync) below.

ClickHouse has no views: the ticks table is the only one there, and nothing reads it through a view.

### trades_history

`trade_type` selects the four trading records:

| `trade_type` | Enum |
|---|---|
| `0` | `kBuy` |
| `1` | `kSell` |
| `14` | `kBuyCancelled` |
| `15` | `kSellCancelled` |

Every column of [`trades`](#trades) is exposed except the identity column.

### cash_transfers

Everything else: `kBalance`, `kCredit`, `kFee`, `kAdjustment`, `kBonus`, `kCommission`, `kDailyCommission`,
`kMonthlyCommission`, `kAgentCommission`, `kDailyAgentCommission`, `kMonthlyAgentCommission`, `kInterest`,
`kDividend`, `kFrankedDividend`, `kTax`, `kStopoutCompensation`, `kStopoutCreditAdjustment` and `kCreditBonus` —
`trade_type` 2 to 13 and 16 to 21 today.

The transfer pipeline populates a fixed subset of the trades columns and this view is that subset. The rest are
left at `0` by the writer and are not exposed, so nothing invites a reader to draw a conclusion from them.

| # | Column | Populated | Source in the trade server |
|---|--------|-----------|----------------------------|
| 1 | `sender_id` | always | export pipeline configuration |
| 2 | `group_name` | always | `group.GetName()` |
| 3 | `symbol_name` | always | the transfer currency |
| 4 | `trade_id` | always | next trade id |
| 5 | `time_created` | always | request timestamp |
| 6 | `time_modified` | always | request timestamp |
| 7 | `trading_account_id` | always | request |
| 8 | `group_id` | always | `group.GetId()` |
| 9 | `trade_type` | always | the transfer type |
| 10 | `notional_value_in_original_currency` | always | amount, in the transfer currency |
| 11 | `notional_value_in_account_currency` | always | amount, in the account currency |
| 12 | `entry_type` | always | `kIn` / `kOut`; for collateral, by the sign of the amount |
| 13 | `reason` | always | `kAdmin` |
| 14 | `account_currency_decimal_places` | always | `group.GetCurrencyDecimalPlaces()` |
| 15 | `comment` | always | request |
| 16 | `volume` | collateral only | amount converted at the symbol's rate |
| 17 | `contract_size` | collateral only | `symbol.GetLotSize()` |
| 18 | `market_bid` | collateral only | top of book |
| 19 | `market_ask` | collateral only | top of book |
| 20 | `symbol_id` | collateral only | `symbol.GetId()` |
| 21 | `side` | collateral only | `kBuy` / `kSell` by the sign of the amount |
| 22 | `price_decimal_places` | collateral only | `symbol.GetDecimalPlaces()` |
| 23 | `time_inserted` | *added by the view* | when the row landed in the database |
| 24 | `is_collateral` | *added by the view* | `symbol_id <> 0` |

A **collateral transfer** is one whose currency differs from the account currency. The trade server resolves a
symbol for it and fills columns 16–22 from that symbol and the top of its book; a plain transfer leaves them at
`0`. `is_collateral` is what tells the two apart — `symbol_id` is the one of the seven that cannot legitimately
be `0` on a collateral transfer.

### trading_accounts_resync

Whether each sender's picture of the live account set is settled or still converging, aggregated from the
resync markers in [`trading_account_events`](#trading_account_events) — `event_type` `2` and `3`. There is no
table behind it: the markers are the record of every resync, so the answer cannot disagree with the log.

| Column | Notes |
|--------|-------|
| `sender_id` | One row per sender that has ever resynced |
| `resync_started_event_row_id` | The log row the newest start marker arrived as — the watermark the end marker compares `source_event_row_id` against |
| `time_resync_started` | When the newest resync started |
| `time_resync_finished` | When a resync last finished. While one is in progress this belongs to the *previous* resync — read it together with `resync_in_progress` |
| `resync_in_progress` | A start marker with no end marker after it |

It matters because [`trading_accounts`](#trading_accounts) carries a mixture between the two markers: accounts
the resync has re-announced are current, accounts it has not reached yet still carry their pre-resync state,
and accounts deleted while the sender was down survive until the end marker sweeps them. Bounded and plausible
rather than wrong — and a reader that cares can tell the two situations apart here.

### symbols_resync

The same answer for the symbol export, aggregated from the resync markers in
[`symbol_events`](#symbol_events), with the same columns and the same meanings as
[`trading_accounts_resync`](#trading_accounts_resync) above. It is a separate view rather than a column on that
one because the two exports are separate pipelines: a sender can be mid-resync on symbols while its trading
account picture is settled, and vice versa.

### Reading a view needs `SELECT` on the view itself

A grant on the underlying table does not reach through. `psql/users/ro.sql` grants all four views; `exporter`
gets none of them, because it writes rows and does not read reports.

---

## ticks

ClickHouse. The top of the order book of one symbol — the best bid and the best ask, each with its volume — one
row per change. The trade server writes a tick when it publishes prices for a symbol and the best bid or the best
ask differs from the previous publication; a change of price and a change of volume both count, a change deeper
in the book does not. Prices include the symbol's markup and not the group markups. A symbol produces at most as
many ticks per second as its `throttling` setting allows, from 1 to 5.

Created by `clickhouse/20260922_01_create_ticks.sql`, column for column from the ticks script of the *Export
Setup* guide:

| Column | Type | Notes |
|--------|------|-------|
| `sender_id` | `UInt64` | Identifies the writing trade server. Every server exporting into one database must use a different value |
| `sequence` | `UInt64` | The order in which this server instance inserted its rows |
| `batch_time` | `DateTime64(6, 'UTC')` | When the batch that carried the row was created. Every row of one insert has the same value |
| `created_time` | `DateTime64(6, 'UTC')` | When the trade server received the latest price update the publication includes |
| `symbol_id` | `UInt64` | Unique identifier of the instrument |
| `short_symbol_name` | `LowCardinality(String)` | Name of the instrument without its path, such as `EURUSD` |
| `bid_price` | `Decimal128(12)` | Best bid price. Zero when the bid side is empty |
| `bid_volume` | `Decimal64(8)` | Volume at the best bid, in the units of the order book. Zero when the bid side is empty |
| `ask_price` | `Decimal128(12)` | Best ask price. Zero when the ask side is empty |
| `ask_volume` | `Decimal64(8)` | Volume at the best ask. Zero when the ask side is empty |

Every column is `NOT NULL`. The three service columns come first, as on every ClickHouse table the trade server
writes; `batch_time` and `created_time` carry `CODEC(Delta, Default)`, because consecutive rows of one symbol
are close in time.

```sql
ENGINE = ReplacingMergeTree          -- ReplicatedReplacingMergeTree with --cluster
PARTITION BY toYYYYMM(created_time)
PRIMARY KEY (sender_id, short_symbol_name)
ORDER BY (sender_id, short_symbol_name, created_time, sequence)
```

| Choice | Why |
|--------|-----|
| `ReplacingMergeTree` | Delivery is at-least-once: after a lost connection the server re-sends the batch it could not confirm. Duplicates collapse on the sorting key when the parts merge, and they are identical rows, so no version column is needed |
| `PRIMARY KEY (sender_id, short_symbol_name)` | The sparse index held in memory stops at the symbol; the sorting key goes on to `created_time` and `sequence` so that two ticks of one symbol never collapse into each other |
| Monthly partitions | Make the TTL cheap: a part is dropped whole once every row in it has expired |

**Reading it**

- Add `FINAL` to read every row once before the parts have merged.
- Sort by `created_time`, not by `sequence`, to read ticks in time order.
- A side of the book with no quotes is a zero price **and** a zero volume — check the volume to tell an empty
  side from a price.
- When a symbol's session closes the server writes one tick with both sides empty, stamped with the time of the
  last price update before the close. After a restart the first tick of every symbol is written even when it
  equals the last one before the restart.

Roles: `exporter` holds `INSERT` on the table and nothing else, `ro` holds `SELECT` —
[Users & Permissions](USERS_AND_PERMISSIONS.md#clickhouse).

---

## schema_versions

The migrator's own bookkeeping. Identical on both engines by design.

| Column | PostgreSQL | ClickHouse |
|--------|------------|------------|
| `script_name` | `TEXT NOT NULL`, primary key | `LowCardinality(String)` |
| `applied_at` | `TIMESTAMP WITH TIME ZONE NOT NULL` | `DateTime64(6, 'UTC')` |

ClickHouse uses `MergeTree` (or `ReplicatedMergeTree` with `--cluster`), ordered by
`(script_name, applied_at)`.

Entry naming:

| Kind | Journal entry | Re-runs when |
|------|---------------|--------------|
| Incremental | `[psql] 20260101_01_create_trades.sql` | never |
| Re-runnable | `[psql/post] run_on_every_build.sql::a1b2…` | its processed content changes |
| Users | `[psql/users] exporter.sql for trade_exporter::7f3c…` | its content, name or password changes |

The `::` suffix is the MD5 of the processed script. Before writing a new entry the migrator deletes every
previous entry for the same script, so exactly one row per re-runnable script survives.

```sql
SELECT script_name, applied_at FROM schema_versions ORDER BY applied_at;
```

---

## Deduplication

Export delivery is at-least-once: after a restart or a lost connection the trade server may resend records it
already sent. Each table has its own key, and the engine that holds the table absorbs the duplicate in its own
way:

| Table | Engine | Key |
|-------|--------|-----|
| Trades | PostgreSQL | `(sender_id, trade_id)` |
| Daily statements | PostgreSQL | `(sender_id, time_statement, trading_account_id)` |
| Order snapshots | PostgreSQL | `(sender_id, time_statement, order_id)` |
| Position snapshots | PostgreSQL | `(sender_id, time_statement, position_id)` |
| Trading account events | PostgreSQL | **none** — see below |
| Symbol events | PostgreSQL | **none** — see below |
| Ticks | ClickHouse | `(sender_id, short_symbol_name, created_time, sequence)` |

| | PostgreSQL | ClickHouse |
|---|------------|------------|
| Mechanism | a `UNIQUE` index on the key | `ReplacingMergeTree` sorted by the key |
| When it applies | immediately, on insert | when parts merge — eventually |
| Effect of a duplicate | second insert rejected | both rows exist until the merge collapses them |
| Reading | always exact | add `FINAL` when duplicates must not be visible |

```sql
-- ClickHouse: exact counts before a merge has happened
SELECT count() FROM ticks FINAL;
```

`FINAL` costs more than a plain read. For aggregate reporting over large ranges, consider deduplicating in the
query (`argMax` by the key) or accepting the small window of duplication instead.

The two event logs are the exception: neither carries a per-event id, so there is no key to deduplicate on and
a redelivered event lands as a second row. Its derived table absorbs that — a re-applied change leaves the same
state — but a consumer reading either log directly can see one logical change twice. Adding a per-event id and a
unique index on `(sender_id, event_id)` later would be purely additive.

---

## Caveats

### Adding an index to a large, live table

The read-path indexes are added by `psql/20260828_01_add_reporting_indexes.sql`, an ordinary incremental script,
so on an empty or small database they are simply built. On a table already holding a serious number of rows they
are not free: `CREATE INDEX` takes an `ACCESS EXCLUSIVE` lock for the whole build and the exporter's inserts
block until it finishes.

`CREATE INDEX CONCURRENTLY` is the usual escape and **it cannot be used from the migrator** — every PostgreSQL
script runs inside one transaction and PostgreSQL rejects `CONCURRENTLY` inside a transaction block with
`25001`. Splitting the script with `----break----` does not change that; it produces several commands in the
same transaction.

The routine is therefore: build them concurrently by hand first, then migrate. Every statement in the script is
`CREATE INDEX IF NOT EXISTS`, so it adopts what is already there and does nothing.

```sql
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_account_time_trades ON trades (trading_account_id, time_created);
```

A cancelled or failed concurrent build leaves an **invalid** index behind, which the planner ignores and
`IF NOT EXISTS` will not replace. Check before trusting it:

```sql
SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid;
```

### `IF NOT EXISTS` everywhere

Every create statement is conditional, so the migrator can be pointed at a database that was provisioned by hand
from the guide before this repository existed. It adopts what is already there rather than failing — which also
means it will **not** correct a table whose columns differ from the script. Compare the schema by hand when
adopting a database you did not create. On ClickHouse that includes the engine: a `ticks` table
created by hand without `--cluster` in mind stays a local table when the flag is added later.

### Renamed tables

If the trade server's export configuration uses non-default table names, every run must pass the same
`--variables trades_table=… daily_statements_table=… order_snapshots_table=… position_snapshots_table=…
cursor_table=…` on PostgreSQL, or `--variables ticks_table=…` on ClickHouse. Nothing records the names on the
database side, so a run without them creates and grants on a second set of tables under the default names.

---

## See also

[Writing Migrations](WRITING_MIGRATIONS.md) · [Users & Permissions](USERS_AND_PERMISSIONS.md) ·
[Template Variables](TEMPLATE_VARIABLES.md) · [Architecture](ARCHITECTURE.md)
