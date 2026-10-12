<div align="center">

# client_events

**Every change to a client, as an ordered log — the source `clients` is derived from**

PostgreSQL · written by the trade server · [Schema Reference](../SCHEMA_REFERENCE.md)

[Overview](#overview) · [Final SQL](#final-sql) · [Event types](#event-types) · [Columns](#columns) · [Values](#values) · [Open points](#open-points) · [Indexes](#indexes) · [Scripts](#scripts)

</div>

---

## Overview

| | |
|---|---|
| Engine | PostgreSQL |
| Default name | `client_events` — renamed with `--variables client_events_table=…` |
| Written by | The trade server's `client` pipeline — `"entity": "client"`, `"target": "postgresql"`. A resync can be requested on demand with the Admin API's `POST /admin/export/snapshot`, `"entityType": "Client"` |
| One row per | Event: a client's full state, a deletion, or a resync marker — in order |
| Order | `id` — never `time_inserted`, which a whole batch shares |
| Deduplicated by | Nothing — there is no deduplication key |
| Grants | `exporter`: `INSERT`, `SELECT` · `ro`: `SELECT` |
| Derived from it | [`clients`](clients.md), by trigger, and the [`clients_resync`](clients.md#clients_resync) view |

When a client is created, modified or deleted, the trade server exports an event for the change; the events form a
log from which a client's complete history can be reconstructed. The same shape as
[`trading_account_events`](trading_account_events.md), for a different entity. Reducing the log to "which clients are
live" is the job of [`clients`](clients.md) — see
[Event logs and their projections](../SCHEMA_REFERENCE.md#event-logs-and-their-projections).

---

## Final SQL

The table as the migrator creates it, with the default names substituted.

```sql
CREATE TABLE client_events
(
    id                              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                       BIGINT                   NOT NULL,

    event_type                      SMALLINT                 NOT NULL,
    client_id                       BIGINT                   NOT NULL,
    version                         BIGINT                   NOT NULL,
    client_type                     INTEGER                  NOT NULL,
    client_status                   INTEGER                  NOT NULL,
    assigned_manager                BIGINT                   NOT NULL,

    time_created                    TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified                   TIMESTAMP WITH TIME ZONE NOT NULL,

    compliance_approved_by          BIGINT                   NOT NULL,
    compliance_client_category      VARCHAR(64)              NOT NULL DEFAULT '',
    compliance_date_approval        TIMESTAMP WITH TIME ZONE NOT NULL,
    compliance_date_termination     TIMESTAMP WITH TIME ZONE NOT NULL,

    lead_campaign                   VARCHAR(64)              NOT NULL DEFAULT '',
    lead_source                     VARCHAR(64)              NOT NULL DEFAULT '',

    introducer                      BIGINT                   NOT NULL,

    person_title                    VARCHAR(32)              NOT NULL DEFAULT '',
    person_first_name               VARCHAR(32)              NOT NULL DEFAULT '',
    person_middle_name              VARCHAR(64)              NOT NULL DEFAULT '',
    person_last_name                VARCHAR(32)              NOT NULL DEFAULT '',
    person_birth_date               TIMESTAMP WITH TIME ZONE NOT NULL,
    person_citizenship              VARCHAR(64)              NOT NULL DEFAULT '',

    person_tax_id                   VARCHAR(64)              NOT NULL DEFAULT '',
    person_document_type            VARCHAR(32)              NOT NULL DEFAULT '',
    person_document_number          VARCHAR(32)              NOT NULL DEFAULT '',
    person_document_date            TIMESTAMP WITH TIME ZONE NOT NULL,
    person_document_expiration_date TIMESTAMP WITH TIME ZONE NOT NULL,
    person_document_extra           VARCHAR(64)              NOT NULL DEFAULT '',

    person_employment               INTEGER                  NOT NULL,
    person_industry                 INTEGER                  NOT NULL,
    person_education                INTEGER                  NOT NULL,
    person_wealth_source            INTEGER                  NOT NULL,
    person_annual_income            DOUBLE PRECISION         NOT NULL,
    person_annual_income_currency   VARCHAR(16)              NOT NULL DEFAULT '',
    person_net_assets               DOUBLE PRECISION         NOT NULL,
    person_gender                   INTEGER                  NOT NULL,

    preferred_communication         INTEGER                  NOT NULL,
    preferred_language              VARCHAR(16)              NOT NULL DEFAULT '',

    company_name                    VARCHAR(64)              NOT NULL DEFAULT '',
    company_registration_number     VARCHAR(64)              NOT NULL DEFAULT '',
    company_registration_date       TIMESTAMP WITH TIME ZONE NOT NULL,
    company_registration_authority  VARCHAR(64)              NOT NULL DEFAULT '',
    company_vat                     VARCHAR(32)              NOT NULL DEFAULT '',
    company_lei                     VARCHAR(32)              NOT NULL DEFAULT '',
    company_license_number          VARCHAR(32)              NOT NULL DEFAULT '',
    company_license_authority       VARCHAR(64)              NOT NULL DEFAULT '',
    company_country                 VARCHAR(32)              NOT NULL DEFAULT '',
    company_registration_address    VARCHAR(64)              NOT NULL DEFAULT '',
    company_website                 VARCHAR(64)              NOT NULL DEFAULT '',

    email                           VARCHAR(64)              NOT NULL DEFAULT '',
    phone                           VARCHAR(32)              NOT NULL DEFAULT '',
    messengers                      VARCHAR(128)             NOT NULL DEFAULT '',
    social_media                    VARCHAR(128)             NOT NULL DEFAULT '',
    last_contact_date               TIMESTAMP WITH TIME ZONE NOT NULL,

    address_country                 VARCHAR(32)              NOT NULL DEFAULT '',
    address_postcode                VARCHAR(16)              NOT NULL DEFAULT '',
    address_street                  VARCHAR(128)             NOT NULL DEFAULT '',
    address_state                   VARCHAR(64)              NOT NULL DEFAULT '',
    address_city                    VARCHAR(64)              NOT NULL DEFAULT '',

    client_external_id              VARCHAR(64)              NOT NULL DEFAULT '',
    comment                         VARCHAR(256)             NOT NULL DEFAULT '',

    experience_forex                INTEGER                  NOT NULL,
    experience_cfd                  INTEGER                  NOT NULL,
    experience_futures              INTEGER                  NOT NULL,
    experience_stocks               INTEGER                  NOT NULL,
    experience_crypto               INTEGER                  NOT NULL
);

-- a client's history
CREATE INDEX idx_client_row_client_events
    ON client_events (sender_id, client_id, id);

-- the resync markers only, for the watermark lookup at the end of a resync
CREATE INDEX idx_markers_client_events
    ON client_events (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
```

---

## Event types

| Value | Name | Meaning | Content columns | `client_id` |
|-------|------|---------|-----------------|-------------|
| `0` | Upsert | The client's current full state — used for a creation and a modification alike | All populated | The client |
| `1` | Delete | The client was removed | Zeros | The deleted client |
| `2` | SnapshotStart | A resync begins: every live client follows as an Upsert | Zeros | `0` |
| `3` | SnapshotEnd | The resync is complete | Zeros | `0` |
| anything else | — | Unknown to this schema — stored, not projected | — | — |

A consumer reading the log directly treats a resync the same way as on
[`trading_account_events`](trading_account_events.md#reading-the-log).

---

## Columns

68 columns: three service columns and 65 data columns, every one `NOT NULL`. On a Delete and on both markers every
content column carries a type-appropriate zero — `0`, `''`, the epoch — rather than `NULL`, so check `event_type`
before reading any of them. The data columns are the trade server's client record as the Admin REST API's `Client`
resource carries it; the descriptions combine the API's and the *Export Setup* guide's.

| Column | Type | Description |
|--------|------|-------------|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY` | Primary key, and the write order: ordering by `id` reconstructs the history |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE` | When the row reached PostgreSQL; defaults to `now()`. A whole batch shares one, so it cannot order events. The column to purge the log by |
| `sender_id` | `BIGINT` | The trade server instance that produced the event, from the pipeline's `sender_id` |
| `event_type` | `SMALLINT` | See [Event types](#event-types) |
| **Client** | | |
| `client_id` | `BIGINT` | API `id`. The client. `0` on both markers. The identifier `trading_accounts.client_id` carries |
| `version` | `BIGINT` | The client's version, incremented on each modification. `0` on a deletion and on both markers |
| `client_type` | `INTEGER` | Individual or Corporate — see [Values](#values) |
| `client_status` | `INTEGER` | Where the client stands with KYC and the broker — see [Values](#values) |
| `assigned_manager` | `BIGINT` | The trading account id of the client's account manager. `0` when unassigned |
| `time_created` | `TIMESTAMP WITH TIME ZONE` | When the client was created — its own creation time, not when the event was exported |
| `time_modified` | `TIMESTAMP WITH TIME ZONE` | When the client's information last changed, as of this event |
| **Compliance and lead** | | |
| `compliance_approved_by` | `BIGINT` | The trading account id of the compliance officer who approved the client |
| `compliance_client_category` | `VARCHAR(64)` | The category the compliance officer assigned |
| `compliance_date_approval` | `TIMESTAMP WITH TIME ZONE` | When the compliance department approved the client |
| `compliance_date_termination` | `TIMESTAMP WITH TIME ZONE` | When the client agreement was terminated |
| `lead_campaign` | `VARCHAR(64)` | The advertising campaign the lead came from |
| `lead_source` | `VARCHAR(64)` | The source of the lead |
| `introducer` | `BIGINT` | The introducing broker's trading account id. `0` means none |
| **Person — an individual client; empty for a corporate one** | | |
| `person_title` | `VARCHAR(32)` | Title |
| `person_first_name` | `VARCHAR(32)` | First name |
| `person_middle_name` | `VARCHAR(64)` | Middle names |
| `person_last_name` | `VARCHAR(32)` | Last name |
| `person_birth_date` | `TIMESTAMP WITH TIME ZONE` | Date of birth |
| `person_citizenship` | `VARCHAR(64)` | Citizenship |
| `person_tax_id` | `VARCHAR(64)` | Tax identifier |
| `person_document_type` | `VARCHAR(32)` | Identification document type — passport, driving licence and so on |
| `person_document_number` | `VARCHAR(32)` | Identification document number |
| `person_document_date` | `TIMESTAMP WITH TIME ZONE` | When the document was issued |
| `person_document_expiration_date` | `TIMESTAMP WITH TIME ZONE` | When the document expires |
| `person_document_extra` | `VARCHAR(64)` | Extra notes on the document |
| `person_employment` | `INTEGER` | Employment status — see [Values](#values) |
| `person_industry` | `INTEGER` | Industry — see [Values](#values) |
| `person_education` | `INTEGER` | Education level — see [Values](#values) |
| `person_wealth_source` | `INTEGER` | Source of wealth — see [Values](#values) |
| `person_annual_income` | `DOUBLE PRECISION` | Average annual income, in `person_annual_income_currency` |
| `person_annual_income_currency` | `VARCHAR(16)` | Currency of both the annual income and the net assets |
| `person_net_assets` | `DOUBLE PRECISION` | Net assets, in `person_annual_income_currency` |
| `person_gender` | `INTEGER` | Gender — see [Values](#values) |
| **Communication** | | |
| `preferred_communication` | `INTEGER` | Preferred channel — see [Values](#values) |
| `preferred_language` | `VARCHAR(16)` | Preferred language |
| **Company — a corporate client; empty for an individual** | | |
| `company_name` | `VARCHAR(64)` | Company name |
| `company_registration_number` | `VARCHAR(64)` | Registration number |
| `company_registration_date` | `TIMESTAMP WITH TIME ZONE` | Registration date |
| `company_registration_authority` | `VARCHAR(64)` | Registration authority |
| `company_vat` | `VARCHAR(32)` | VAT payer id |
| `company_lei` | `VARCHAR(32)` | LEI — Legal Entity Identifier |
| `company_license_number` | `VARCHAR(32)` | Licence number |
| `company_license_authority` | `VARCHAR(64)` | Name of the licensing authority |
| `company_country` | `VARCHAR(32)` | Country of incorporation |
| `company_registration_address` | `VARCHAR(64)` | Registered address |
| `company_website` | `VARCHAR(64)` | Website |
| **Contact** | | |
| `email` | `VARCHAR(64)` | Email address |
| `phone` | `VARCHAR(32)` | Phone number |
| `messengers` | `VARCHAR(128)` | Instant messenger contacts, comma-separated |
| `social_media` | `VARCHAR(128)` | Social media accounts, comma-separated |
| `last_contact_date` | `TIMESTAMP WITH TIME ZONE` | When the client was last contacted |
| **Address** | | |
| `address_country` | `VARCHAR(32)` | Country |
| `address_postcode` | `VARCHAR(16)` | Postal code |
| `address_street` | `VARCHAR(128)` | Street address |
| `address_state` | `VARCHAR(64)` | State or province |
| `address_city` | `VARCHAR(64)` | City |
| **Metadata** | | |
| `client_external_id` | `VARCHAR(64)` | The client's id in the external trading system |
| `comment` | `VARCHAR(256)` | Free text |
| **Trading experience** | | |
| `experience_forex` | `INTEGER` | Forex trading experience — see the units under [Open points](#open-points). `-1` not answered, `0` none |
| `experience_cfd` | `INTEGER` | CFD trading experience. `-1` not answered, `0` none |
| `experience_futures` | `INTEGER` | Futures trading experience. `-1` not answered, `0` none |
| `experience_stocks` | `INTEGER` | Stock trading experience. `-1` not answered, `0` none |
| `experience_crypto` | `INTEGER` | Cryptocurrency trading experience. `-1` not answered, `0` none |

On a Delete and on both markers the experience columns are `0`, not `-1`. The API takes the experience fields as
optional and never below `0`; the `-1` is how the export writes one the client left out.

The text columns carry `DEFAULT ''`, which `clients` does not. Most of a client's content is conditional — the
`person_` columns are populated for an individual and the `company_` ones for a corporate client — so a row written
into the log by hand legitimately leaves most of them alone, whereas the projection always writes every column and a
default there would only mask one it had forgotten. Each `VARCHAR` is one wider than the API's maximum length: a
field capped at 63 characters gets `VARCHAR(64)`.

`person_annual_income` and `person_net_assets` are `DOUBLE PRECISION` — the one place an amount is not `NUMERIC` in
this schema. They are client-declared figures rather than ledger amounts, and the type is the one the exporter
writes.

> **Note:** **There is no deduplication key**, as with `trading_account_events`. An event that reaches the database
> twice lands as a second row rather than being rejected. `clients` absorbs that — a re-applied change leaves the
> same state — but a consumer reading this log directly can see one logical change twice.

> **Note:** The API's `phonePassword` is not exported, and leaving it out of the schema is what keeps it out of the
> database — the `ro` reporting role can read every table here. It matches the trading account password, which
> `trading_account_events` omits for the same reason.

---

## Values

Eight columns hold the trade server's `ClientConfig` enum **ordinal**, not its name — the same convention as
`trade_type` and `reason` in [`trades`](trades.md#values). The numbers are the *Export Setup* guide's. The Admin API
sends the names and leaves `Unspecified` out — a field the client did not fill is simply absent there — which is why
its order cannot be read as the ordinal.

| Column | Values |
|--------|--------|
| `client_type` | `0` Unspecified, `1` Individual, `2` Corporate |
| `client_status` | `0` Registered, `1` NotInterested, `2` NeedMoreInfo, `3` Active, `4` Suspended, `5` Rejected, `6` Closed, `7` Terminated |
| `person_employment` | `0` Unspecified, `1` Unemployed, `2` Employed, `3` SelfEmployed, `4` Retired, `5` Student, `6` Other |
| `person_industry` | `0` Unspecified, `1` Agriculture, `2` Construction, `3` Management, `4` Communication, `5` Education, `6` Government, `7` Healthcare, `8` Tourism, `9` Financial, `10` IT, `11` Security, `12` Manufacturing, `13` Entertainment, `14` Science, `15` Transportation, `16` Engineering, `17` Trade, `18` Other |
| `person_education` | `0` Unspecified, `1` Secondary, `2` Bachelor, `3` Master, `4` PhD, `5` Other |
| `person_wealth_source` | `0` Unspecified, `1` Employment, `2` Business, `3` Savings, `4` Inheritance, `5` Other |
| `person_gender` | `0` Unspecified, `1` Male, `2` Female, `3` Other |
| `preferred_communication` | `0` Unspecified, `1` Email, `2` Phone, `3` SMS, `4` Messenger |

`client_status` is the one without an Unspecified member, so its `0` — which a Delete and both markers carry — reads
as Registered. Check `event_type` first. A value this table does not list is stored like any other integer: nothing
validates the range, so a member added later cannot fail the export.

### What the members mean

**`client_status`**

| Value | Name | Meaning |
|-------|------|---------|
| `0` | Registered | Registered; KYC not passed |
| `1` | NotInterested | A lead that is not interested in becoming a client |
| `2` | NeedMoreInfo | More documents are needed to pass KYC |
| `3` | Active | KYC passed |
| `4` | Suspended | Temporarily suspended |
| `5` | Rejected | The account opening application was rejected, or KYC not passed |
| `6` | Closed | Closed by the client |
| `7` | Terminated | Closed by the broker |

**`person_industry`**

| Value | Name | Meaning |
|-------|------|---------|
| `1` | Agriculture | Agriculture, forestry and fishing |
| `2` | Construction | Architecture and construction |
| `3` | Management | Administration and business management |
| `4` | Communication | Audio/video technology and communication |
| `5` | Education | Education and training |
| `6` | Government | State and administrative management |
| `7` | Healthcare | Human health and social work |
| `8` | Tourism | Tourism, accommodation and food service |
| `9` | Financial | Financial and insurance activities |
| `10` | IT | Information technology |
| `11` | Security | Legal and public safety, correction and protection services |
| `12` | Manufacturing | Manufacturing and production |
| `13` | Entertainment | Arts, entertainment and recreation |
| `14` | Science | Professional, scientific and technical activities |
| `15` | Transportation | Transportation and storage |
| `16` | Engineering | Engineering and mathematics |
| `17` | Trade | Wholesale and retail trade |
| `18` | Other | Any other industry |

**`person_education`** — `1` Secondary: secondary school or equivalent · `2` Bachelor: a bachelor's degree or
equivalent · `3` Master: a master's degree or equivalent · `4` PhD: a PhD or equivalent · `5` Other.

---

## Open points

The *Export Setup* guide and the Admin REST API document the client export, and two points are not settled by this
table:

| | *Export Setup* guide | Admin REST API | This table |
|---|---|---|---|
| **`tags` column** | `tags VARCHAR(512) NOT NULL DEFAULT ''`, the last column — free-text client tags | `tags`, up to 511 characters | No such column. The exporter writes its columns by name, and a column it writes that the table lacks fails every batch — so once the trade server writes `tags`, every client batch is dropped until the column exists here and in `clients` |
| **Trading experience** | In **months**; `-1` unspecified, `0` no experience | "Number of years of experience", at least `0`, optional | `INTEGER`; the unit is not constrained |

The guide and the API contradict each other on the unit of the experience columns; this page follows the guide's
`-1` convention, which the API's optional field is consistent with.

`introducer` is a trading account id, as are `assigned_manager` and `compliance_approved_by`: the API says so for
all three. The guide's "client id" for it reads as a slip.

---

## Indexes

| Index | Definition | Purpose |
|-------|------------|---------|
| `client_events_pkey` | `PRIMARY KEY (id)` | |
| `idx_client_row_client_events` | `(sender_id, client_id, id)` | A client's history in recorded order; the projection's reconcile pass |
| `idx_markers_client_events` | `(sender_id, event_type, id) WHERE event_type IN (2, 3)` | The resync markers only, for the watermark the end marker looks up |

---

## Scripts

| Script | Phase | Contributes |
|--------|-------|-------------|
| [`psql/20261002_01_create_client_events.sql`](../../psql/20261002_01_create_client_events.sql) | incremental | The table and both indexes |
| [`psql/views/clients_projection.sql`](../../psql/views/clients_projection.sql) | views | The `AFTER INSERT` trigger on this table — see [`clients`](clients.md#projection) |
| [`psql/views/clients_resync.sql`](../../psql/views/clients_resync.sql) | views | The resync view over the markers |
| [`psql/users/exporter.sql`](../../psql/users/exporter.sql), [`psql/users/ro.sql`](../../psql/users/ro.sql) | users | The grants |

Column descriptions follow the Admin REST API's `Client` and the *Export Setup* guide's Client Events
section; the enum values are the guide's.

---

## See also

[Schema Reference](../SCHEMA_REFERENCE.md) · [`clients`](clients.md) · [`trading_account_events`](trading_account_events.md)
