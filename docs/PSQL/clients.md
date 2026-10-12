<div align="center">

# clients

**The current state of every live client — derived from `client_events` by a trigger**

PostgreSQL · maintained by the migrator's trigger, never by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Columns](#columns) · [Projection](#projection) · [clients_resync](#clients_resync) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `clients` — renamed with `--variables clients_table=…` |
| Written by | The trigger `trg_project_clients` on [`client_events`](client_events.md), in the same transaction as the event |
| One row per | Live client, keyed by `(sender_id, client_id)` |
| Key | `PRIMARY KEY (sender_id, client_id)` |
| Grants | `exporter`: `INSERT`, `SELECT`, `UPDATE`, `DELETE` — the trigger runs as the exporter · `ro`: `SELECT` |
| Companion view | [`clients_resync`](#clients_resync) — whether a resync is in progress, per sender |

"Which clients are live and what do we hold on each" is a single read here: name, status, compliance category,
contact details, address, declared finances and trading experience. The table is **not written by the trade
server**: it is derived from the event log by a trigger inside the same transaction as the event that caused the
change, so it cannot lag behind the log, and a batch that fails to commit leaves no trace in it.

`client_id` is the identifier [`trading_accounts`](trading_accounts.md) carries under that name, so the two derived
tables join directly — a client's accounts are the `trading_accounts` rows whose `sender_id` **and** `client_id`
match. Both halves are required: client `1002` on one instance and client `1002` on another are different people,
often with different names. See [Every id is scoped to its sender](../SCHEMA_REFERENCE.md#every-id-is-scoped-to-its-sender).

```sql
SELECT c.sender_id, c.client_id, c.person_last_name, count(a.trading_account_id) AS accounts
FROM clients c
         LEFT JOIN trading_accounts a ON a.sender_id = c.sender_id AND a.client_id = c.client_id
GROUP BY c.sender_id, c.client_id, c.person_last_name;
```

`sender_id` is in the `GROUP BY` as well as the join. Grouping it away would merge two instances' clients that share
a `client_id` into one row and add their account counts together — the same mistake as joining without it, one step
later.

---

## Final SQL

The table and its companion view as the migrator creates them, with the default names substituted. The projection
function and trigger are under [Projection](#projection).

```sql
CREATE TABLE clients
(
    sender_id                       BIGINT                   NOT NULL,
    client_id                       BIGINT                   NOT NULL,
    version                         BIGINT                   NOT NULL,
    client_type                     INTEGER                  NOT NULL,
    client_status                   INTEGER                  NOT NULL,
    assigned_manager                BIGINT                   NOT NULL,

    time_created                    TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified                   TIMESTAMP WITH TIME ZONE NOT NULL,

    compliance_approved_by          BIGINT                   NOT NULL,
    compliance_client_category      VARCHAR(64)              NOT NULL,
    compliance_date_approval        TIMESTAMP WITH TIME ZONE NOT NULL,
    compliance_date_termination     TIMESTAMP WITH TIME ZONE NOT NULL,

    lead_campaign                   VARCHAR(64)              NOT NULL,
    lead_source                     VARCHAR(64)              NOT NULL,

    introducer                      BIGINT                   NOT NULL,

    person_title                    VARCHAR(32)              NOT NULL,
    person_first_name               VARCHAR(32)              NOT NULL,
    person_middle_name              VARCHAR(64)              NOT NULL,
    person_last_name                VARCHAR(32)              NOT NULL,
    person_birth_date               TIMESTAMP WITH TIME ZONE NOT NULL,
    person_citizenship              VARCHAR(64)              NOT NULL,

    person_tax_id                   VARCHAR(64)              NOT NULL,
    person_document_type            VARCHAR(32)              NOT NULL,
    person_document_number          VARCHAR(32)              NOT NULL,
    person_document_date            TIMESTAMP WITH TIME ZONE NOT NULL,
    person_document_expiration_date TIMESTAMP WITH TIME ZONE NOT NULL,
    person_document_extra           VARCHAR(64)              NOT NULL,

    person_employment               INTEGER                  NOT NULL,
    person_industry                 INTEGER                  NOT NULL,
    person_education                INTEGER                  NOT NULL,
    person_wealth_source            INTEGER                  NOT NULL,
    person_annual_income            DOUBLE PRECISION         NOT NULL,
    person_annual_income_currency   VARCHAR(16)              NOT NULL,
    person_net_assets               DOUBLE PRECISION         NOT NULL,
    person_gender                   INTEGER                  NOT NULL,

    preferred_communication         INTEGER                  NOT NULL,
    preferred_language              VARCHAR(16)              NOT NULL,

    company_name                    VARCHAR(64)              NOT NULL,
    company_registration_number     VARCHAR(64)              NOT NULL,
    company_registration_date       TIMESTAMP WITH TIME ZONE NOT NULL,
    company_registration_authority  VARCHAR(64)              NOT NULL,
    company_vat                     VARCHAR(32)              NOT NULL,
    company_lei                     VARCHAR(32)              NOT NULL,
    company_license_number          VARCHAR(32)              NOT NULL,
    company_license_authority       VARCHAR(64)              NOT NULL,
    company_country                 VARCHAR(32)              NOT NULL,
    company_registration_address    VARCHAR(64)              NOT NULL,
    company_website                 VARCHAR(64)              NOT NULL,

    email                           VARCHAR(64)              NOT NULL,
    phone                           VARCHAR(32)              NOT NULL,
    messengers                      VARCHAR(128)             NOT NULL,
    social_media                    VARCHAR(128)             NOT NULL,
    last_contact_date               TIMESTAMP WITH TIME ZONE NOT NULL,

    address_country                 VARCHAR(32)              NOT NULL,
    address_postcode                VARCHAR(16)              NOT NULL,
    address_street                  VARCHAR(128)             NOT NULL,
    address_state                   VARCHAR(64)              NOT NULL,
    address_city                    VARCHAR(64)              NOT NULL,

    client_external_id              VARCHAR(64)              NOT NULL,
    comment                         VARCHAR(256)             NOT NULL,

    experience_forex                INTEGER                  NOT NULL,
    experience_cfd                  INTEGER                  NOT NULL,
    experience_futures              INTEGER                  NOT NULL,
    experience_stocks               INTEGER                  NOT NULL,
    experience_crypto               INTEGER                  NOT NULL,

    -- the event row this state came from
    source_event_row_id             BIGINT                   NOT NULL,
    -- when the projection last wrote the row
    time_applied                    TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),

    PRIMARY KEY (sender_id, client_id)
);

CREATE VIEW clients_resync AS
SELECT sender_id,
       max(id) FILTER (WHERE event_type = 2)            AS resync_started_event_row_id,
       max(time_inserted) FILTER (WHERE event_type = 2) AS time_resync_started,
       max(time_inserted) FILTER (WHERE event_type = 3) AS time_resync_finished,
       COALESCE(max(id) FILTER (WHERE event_type = 2), -1) >
       COALESCE(max(id) FILTER (WHERE event_type = 3), -1) AS resync_in_progress
FROM client_events
WHERE event_type IN (2, 3)
GROUP BY sender_id;
```

---

## Columns

Every client column of [`client_events`](client_events.md#columns), with the same types, in the same order and with
the same meanings, minus the log's own envelope (`id`, `time_inserted`, `event_type`) and plus:

| Column | Type | Notes |
|--------|------|-------|
| `sender_id`, `client_id` | `BIGINT` | `PRIMARY KEY (sender_id, client_id)` |
| `source_event_row_id` | `BIGINT` | The `client_events.id` this state came from. Traceability, and what a resync's end marker compares against |
| `time_applied` | `TIMESTAMP WITH TIME ZONE` | When the projection last wrote the row, as against `time_modified`, when the client last changed |

Every column is `NOT NULL`, and none but `time_applied` has a default: the projection always writes every column,
and a default would only mask one it had forgotten. The column descriptions, the enum values and the units of the
experience columns are on [`client_events`](client_events.md#columns).

> **Note:** If the `tags` column the *Export Setup* guide lists is added to `client_events` — see its
> [open points](client_events.md#open-points) — it has to be added here too, and to the projection's four column
> lists, or the copy-every-field test fails.

There is deliberately **no foreign key** on `source_event_row_id`, for the same reason as on
[`trading_accounts`](trading_accounts.md#columns): an event ages out of the retention window long before the client
does.

No indexes beyond the primary key. The table is bounded at roughly the live client count — fewer rows than there are
trading accounts, since a client may hold several — so a filter the primary key cannot serve is a scan of a small
table. An index on `email`, `client_status` or `assigned_manager` is the plausible next candidate, left until a real
workload asks for one.

---

## Projection

[`psql/views/clients_projection.sql`](../../psql/views/clients_projection.sql) creates the function `project_clients()` and this trigger on the log:

```sql
CREATE TRIGGER trg_project_clients
    AFTER INSERT
    ON client_events
    FOR EACH ROW
EXECUTE FUNCTION project_clients();
```

It behaves exactly as [the trading account projection](trading_accounts.md#projection) does, against the client
tables:

| `event_type` | What the trigger does |
|--------------|-----------------------|
| `0` Upsert | Upserts the client's row, stamped with the event's `id` |
| `1` Delete | Deletes the client's row |
| `2` SnapshotStart | **Nothing.** The row's own `id` is the watermark the end marker reads back out of the log |
| `3` SnapshotEnd | Deletes that sender's rows stamped older than the newest SnapshotStart before it |
| anything else | Nothing — stored in the log, not projected, and no batch fails |

The script is re-runnable and ends with the same reconcile pass, which also applies a resync sweep the trigger missed
because it was absent when the end marker arrived — see
[Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections).

> **Note:** The projection names sixty-five columns four times over — the `VALUES` list and the `ON CONFLICT` `SET`
> list in the function, then both again in the reconcile. Reading those four lists against each other is not a
> realistic review, so the guard is a test: *Should copy every field from the event unchanged* in
> `ClientsProjectionIntegrationTests` compares the whole log row against the whole state row as `jsonb`, and so fails
> on any column the file crosses, drops or leaves behind.

---

## clients_resync

The same answer as [`trading_accounts_resync`](trading_accounts.md#trading_accounts_resync), for the client export,
aggregated from the resync markers in [`client_events`](client_events.md#event-types) — the same columns with the
same meanings. It is a separate view because the two exports are separate pipelines: a sender can be mid-resync on
clients while its trading account picture is settled, and vice versa.

| | |
|---|---|
| Default name | `clients_resync` — renamed with `--variables clients_resync_view=…` |
| Grants | `ro`: `SELECT` · `exporter`: none |

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20261002_02_create_clients.sql`](../../psql/20261002_02_create_clients.sql) | incremental | The table |
| [`psql/views/clients_projection.sql`](../../psql/views/clients_projection.sql) | views | `project_clients()`, `trg_project_clients` and the reconcile pass |
| [`psql/views/clients_resync.sql`](../../psql/views/clients_resync.sql) | views | The `clients_resync` view |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`client_events`](client_events.md) · [`trading_accounts`](trading_accounts.md)
