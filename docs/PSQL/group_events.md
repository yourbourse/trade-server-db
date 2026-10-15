<div align="center">

# group_events

**Every change to a group's configuration, as an ordered log — the source `groups` is derived from**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Event types](#event-types) · [Columns](#columns) · [Values](#values) · [Indexes](#indexes) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `group_events` — renamed with `--variables group_events_table=…` |
| Written by | The trade server's group export pipeline. A resync can be requested on demand with the Admin API's `POST /admin/export/snapshot`, `"entityType": "Group"` |
| One row per | Event: a group's full configuration, a deletion, or a resync marker |
| Order | `id` — never `time_inserted`, which a whole batch shares |
| Deduplicated by | Nothing — there is no deduplication key |
| Grants | `exporter`: `INSERT`, `SELECT` · `ro`: `SELECT` |
| Derived from it | [`groups`](groups.md), by trigger, and the [`groups_resync`](groups.md#groups_resync) view |

The same shape as [`trading_account_events`](trading_account_events.md), for a different entity: a creation or a
modification carries the group's full configuration, a deletion carries none of it, and two marker types bracket a
resync. Reducing this log to "which groups are live and how is each configured" is the job of [`groups`](groups.md)
— see [Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections).

The column set, the types, the nullability and the column order are the schema agreed with the trade server team,
and *Schema Should carry the agreed columns and types in the agreed order* in `GroupsIntegrationTests` pins them.

---

## Final SQL

The table as the migrator creates it, with the default names substituted.

```sql
CREATE TABLE group_events
(
    id                            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                 TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                     BIGINT                   NOT NULL,

    event_type                    SMALLINT                 NOT NULL,
    group_id                      BIGINT                   NOT NULL,
    version                       BIGINT                   NOT NULL,
    name                          VARCHAR(128)             NOT NULL DEFAULT '',
    company                       VARCHAR(64)              NOT NULL DEFAULT '',
    -- NULL or 0: no emails sent
    email_service_id              BIGINT                   NULL,
    currency                      VARCHAR(16)              NOT NULL DEFAULT '',
    currency_decimal_places       INTEGER                  NOT NULL DEFAULT 0,
    interest_rate                 NUMERIC                  NOT NULL DEFAULT 0,
    free_margin_mode              INTEGER                  NOT NULL DEFAULT 0,
    stopout_value_type            INTEGER                  NOT NULL DEFAULT 0,
    stopout                       NUMERIC                  NOT NULL DEFAULT 0,
    margin_call_value_type        INTEGER                  NOT NULL DEFAULT 0,
    margin_call                   NUMERIC                  NOT NULL DEFAULT 0,
    open_position_type            INTEGER                  NOT NULL DEFAULT 0,
    exchange_trading_mode         SMALLINT                 NOT NULL DEFAULT 0,
    default_leverage              INTEGER                  NOT NULL DEFAULT 0,
    demo_deposit                  NUMERIC                  NOT NULL DEFAULT 0,
    demo_inactivity_period        INTEGER                  NOT NULL DEFAULT 0,
    max_trade_history_period      INTEGER                  NOT NULL DEFAULT 0,
    max_orders                    INTEGER                  NOT NULL DEFAULT 0,
    max_positions                 INTEGER                  NOT NULL DEFAULT 0,
    max_market_data_subscriptions INTEGER                  NOT NULL DEFAULT 0,
    send_daily_reports            BOOLEAN                  NOT NULL DEFAULT false,
    send_monthly_reports          BOOLEAN                  NOT NULL DEFAULT false,
    -- list lengths only; the lists are not exported
    symbol_group_profile_count    BIGINT                   NOT NULL DEFAULT 0,
    commission_profile_count      BIGINT                   NOT NULL DEFAULT 0
);

-- a group's history
CREATE INDEX idx_group_row_group_events
    ON group_events (sender_id, group_id, id);

-- the resync markers only, for the watermark lookup at the end of a resync
CREATE INDEX idx_markers_group_events
    ON group_events (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
```

---

## Event types

| `event_type` | Meaning | Content columns | `group_id` | `version` |
|--------------|---------|-----------------|------------|-----------|
| `0` | Group state — a creation or a modification | The full configuration | The group | The group's version |
| `1` | Deletion | Zeros | The group | `0` |
| `2` | Resync started | Zeros | `0` | `0` |
| `3` | Resync finished | Zeros | `0` | `0` |
| anything else | Unknown to this schema — stored, not projected | — | — | — |

The values are assumed to match the trading account and client logs, where the *Export Setup* guide names them
Upsert, Delete, SnapshotStart and SnapshotEnd; for groups the trade server team has yet to confirm them.

---

## Columns

30 columns: a four-column envelope, the group's id, and 25 configuration fields, in the agreed order. The
configuration fields are the trade server's group configuration as the Admin REST API's `Group` resource carries it,
and the descriptions are the API's.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key, and the write order: ordering by `id` reconstructs the history |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row reached PostgreSQL; defaults to `now()`. A whole batch shares one, so it cannot order events |
| `sender_id` | `BIGINT` | The trade server instance that produced the event |
| `event_type` | `SMALLINT` | See [Event types](#event-types) |
| `group_id` | `BIGINT` | API `id`. The group. `0` on both markers. The identifier `trading_accounts`, `trades`, the statements and the snapshots carry under that name |
| `version` | `BIGINT` | The group's version as of this event. `0` on a deletion and on both markers |
| `name` | `VARCHAR(128)` | The group's name with its full path, for example `real/FPlive/CHF_IN_NT_R_H`. What `trades`, the statements and the snapshots carry as `group_name` |
| `company` | `VARCHAR(64)` | Company name |
| `email_service_id` | `BIGINT`, **nullable** | The email service configuration that sends the group's reports and statements. `NULL` or `0`: no emails are sent |
| `currency` | `VARCHAR(16)` | The account currency of the group's accounts |
| `currency_decimal_places` | `INTEGER` | Precision of the account currency, `0`–`12` |
| `interest_rate` | `NUMERIC` | Annual interest rate on free funds |
| `free_margin_mode` | `INTEGER` | How floating profit and loss count towards free margin — see [Values](#values) |
| `stopout_value_type` | `INTEGER` | Whether `stopout` is a percentage or an amount — see [Values](#values) |
| `stopout` | `NUMERIC` | Open positions are closed when this threshold is breached |
| `margin_call_value_type` | `INTEGER` | Whether `margin_call` is a percentage or an amount — see [Values](#values) |
| `margin_call` | `NUMERIC` | The client is due a margin call when this threshold is breached |
| `open_position_type` | `INTEGER` | Netting or hedged positions — see [Values](#values) |
| `exchange_trading_mode` | `SMALLINT` | How exchange-traded symbols settle — see [Values](#values). The one enum column that is a `SMALLINT` |
| `default_leverage` | `INTEGER` | Default leverage, at least `1`. Can be overridden per account |
| `demo_deposit` | `NUMERIC` | The first deposit of an account created in a group whose name contains `demo` |
| `demo_inactivity_period` | `INTEGER` | Days without a login after which a demo account is disabled and its trading history erased. `0` disables the check |
| `max_trade_history_period` | `INTEGER` | How many days of trade history a user may request. `0` gives access to the full history |
| `max_orders` | `INTEGER` | Most active orders at any time. `0` disables the check |
| `max_positions` | `INTEGER` | Most open positions at any time. `0` disables the check |
| `max_market_data_subscriptions` | `INTEGER` | Most market data subscriptions at any time. `0` disables the check |
| `send_daily_reports` | `BOOLEAN` | Whether daily reports are generated and sent |
| `send_monthly_reports` | `BOOLEAN` | Whether monthly reports are generated and sent |
| `symbol_group_profile_count` | `BIGINT` | How many entries the group's `symbols` list has — see [the lists](#the-two-lists-exported-as-counts) |
| `commission_profile_count` | `BIGINT` | How many entries the group's `commissionProfiles` list has |

Every column but `email_service_id` is `NOT NULL`, and the exporter writes every one of them on every row — a field
that does not apply carries a type-appropriate zero (`''`, `0`, `false`). A delete row carries those zeros in every
content column, `version` included, while `group_id` still names the group; both marker rows carry them in `group_id`
too — see
[Event types](#event-types). That is why a delete row cannot be ordered into a group's history by its `version`. Use
`id`.

`email_service_id` is nullable because the API's `emailServiceId` is optional. The trade server treats `NULL` and
`0` alike — no email service, so no emails — and a reader asking whether a group sends reports has to test for both.

Thresholds, rates and deposits are unconstrained `NUMERIC`, as in [`trades`](trades.md): a value is stored exactly
as the trade server sent it, with no floating-point rounding on the way in or out.

The columns carry the defaults the agreed schema declares; `groups` does not, because the projection always writes
every column and a default there would only mask one it had forgotten.

### The two lists exported as counts

The group's two lists are exported as their lengths only. Neither the log nor `groups` records their content, so
which symbols a group trades on what terms, and what it is charged, cannot be answered from here — only how many
entries each list had.

| API list | Counted in | Each entry |
|----------|------------|------------|
| `symbols` | `symbol_group_profile_count` | Overrides for the symbols matching a symbol mask: trade mode, order sizes, allowed order types and times in force, extra bid and ask markup, quotes timeout, margins and margin rates, swaps and their mode, market data delay |
| `commissionProfiles` | `commission_profile_count` | A charge on the symbols matching a symbol mask: its type — commission, agent or fee — the range it is tiered by, when it is charged (daily, monthly, instant), which entries, sides and outcomes it applies to, and its tiers |

### Where a column name differs from the API

| API field | Column |
|-----------|--------|
| `id` | `group_id` |
| `symbols` | `symbol_group_profile_count` — the list's length only |
| `commissionProfiles` | `commission_profile_count` — the list's length only |

Every other field is the snake case of its API name — `freeMarginMode` is `free_margin_mode`,
`maxMarketDataSubscriptions` is `max_market_data_subscriptions`.

> **Note:** **There is no deduplication key**, as with `trading_account_events`. An event that reaches the
> database twice lands as a second row rather than being rejected. `groups` absorbs that — a re-applied change
> leaves the same state — but a consumer reading this log directly can see one logical change twice.

---

## Values

Five columns hold the trade server's enum **ordinal**; the Admin API sends the member's name. The members and their
meanings below are the API's. Their numbers are left out on purpose: the API's order is no proof of the ordinal — its
client enums omit the `Unspecified` member the export numbers `0` — so they are to be confirmed against the trade
server's enums. Nothing validates the range, so a member added later cannot fail the export.

**`free_margin_mode`**

| Member | Free margin |
|--------|-------------|
| UseFloatingPL | Both floating profit and floating loss on open positions count |
| UseFloatingProfit | Only floating profit is added |
| UseFloatingLoss | Only floating loss is deducted |

**`stopout_value_type`** and **`margin_call_value_type`**

| Member | `stopout` / `margin_call` is |
|--------|------------------------------|
| Percentage | A margin level, in percent — margin level = equity ÷ margin × 100%. The threshold is breached when the margin level drops below it |
| Money | An amount in the account currency. The threshold is breached when equity drops below it |

**`open_position_type`**

| Member | |
|--------|---|
| Netting | All open positions are netted off automatically |
| Hedged | Positions in opposite directions may be held at once |

**`exchange_trading_mode`** — how exchange-traded symbols settle; OTC instruments are unaffected. The API makes it
optional, `Margin` when omitted, and does not enforce it yet: it is stored and returned with no effect on trading.

| Member | |
|--------|---|
| Margin | A negative balance is permitted, secured by exchange assets; short selling of exchange symbols is allowed |
| Cash | The balance cannot go negative from exchange trades; short selling of exchange symbols is disabled, and an order that would take the balance negative is rejected |

---

## Indexes

| Index | Definition | Purpose |
|-------|------------|---------|
| `group_events_pkey` | `PRIMARY KEY (id)` | |
| `idx_group_row_group_events` | `(sender_id, group_id, id)` | A group's history in recorded order; the projection's reconcile pass |
| `idx_markers_group_events` | `(sender_id, event_type, id) WHERE event_type IN (2, 3)` | The resync markers only, for the watermark the end marker looks up |

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20261007_01_create_group_events.sql`](../../psql/20261007_01_create_group_events.sql) | incremental | The table and both indexes |
| [`psql/views/groups_projection.sql`](../../psql/views/groups_projection.sql) | views | The `AFTER INSERT` trigger on this table — see [`groups`](groups.md#projection) |
| [`psql/views/groups_resync.sql`](../../psql/views/groups_resync.sql) | views | The resync view over the markers |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`groups`](groups.md) · [`trading_accounts`](trading_accounts.md)
