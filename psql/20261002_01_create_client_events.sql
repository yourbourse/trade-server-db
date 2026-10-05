-- Client lifecycle events exported by the trade server; ##clients_table## is derived from it.
--
-- Order by id, not time_inserted: every event of a batch shares one time_inserted.
-- Columns that do not apply carry a zero ('' / 0 / epoch), never NULL. Most of a client's content is
-- conditional on client_type, which is why the text columns default to '' here and not in ##clients_table##.
-- The enum columns hold the trade server's ClientConfig ordinals, as trade_type and side do in ##trades_table##;
-- docs/SCHEMA_REFERENCE.md lists the names.
-- Column order and types follow the schema agreed with the trade server team, so a positional COPY also lines up.
-- No deduplication key: delivery is at-least-once, so a redelivered event lands as a second row.

CREATE TABLE IF NOT EXISTS ##client_events_table##
(
    id                              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                       BIGINT                   NOT NULL,

    -- top-level
    -- 0 client state, 1 deletion, 2 resync started, 3 resync finished; unknown values are stored, not projected
    event_type                      SMALLINT                 NOT NULL,
    -- 0 on both marker rows
    client_id                       BIGINT                   NOT NULL,
    -- the client's version as of this event; 0 on a deletion and on both marker rows
    version                         BIGINT                   NOT NULL,
    client_type                     INTEGER                  NOT NULL,
    client_status                   INTEGER                  NOT NULL,
    -- the trading account id of the assigned manager; 0 when unassigned
    assigned_manager                BIGINT                   NOT NULL,

    -- timestamps
    time_created                    TIMESTAMP WITH TIME ZONE NOT NULL,
    time_modified                   TIMESTAMP WITH TIME ZONE NOT NULL,

    -- compliance
    compliance_approved_by          BIGINT                   NOT NULL,
    compliance_client_category      VARCHAR(64)              NOT NULL DEFAULT '',
    compliance_date_approval        TIMESTAMP WITH TIME ZONE NOT NULL,
    compliance_date_termination     TIMESTAMP WITH TIME ZONE NOT NULL,

    -- lead
    lead_campaign                   VARCHAR(64)              NOT NULL DEFAULT '',
    lead_source                     VARCHAR(64)              NOT NULL DEFAULT '',

    -- relationships
    -- the introducing broker's trading account id; 0 means none
    introducer                      BIGINT                   NOT NULL,

    -- person identity; empty throughout for a corporate client
    person_title                    VARCHAR(32)              NOT NULL DEFAULT '',
    person_first_name               VARCHAR(32)              NOT NULL DEFAULT '',
    person_middle_name              VARCHAR(64)              NOT NULL DEFAULT '',
    person_last_name                VARCHAR(32)              NOT NULL DEFAULT '',
    person_birth_date               TIMESTAMP WITH TIME ZONE NOT NULL,
    person_citizenship              VARCHAR(64)              NOT NULL DEFAULT '',

    -- person documents
    person_tax_id                   VARCHAR(64)              NOT NULL DEFAULT '',
    person_document_type            VARCHAR(32)              NOT NULL DEFAULT '',
    person_document_number          VARCHAR(32)              NOT NULL DEFAULT '',
    person_document_date            TIMESTAMP WITH TIME ZONE NOT NULL,
    person_document_expiration_date TIMESTAMP WITH TIME ZONE NOT NULL,
    person_document_extra           VARCHAR(64)              NOT NULL DEFAULT '',

    -- person employment and finances
    person_employment               INTEGER                  NOT NULL,
    person_industry                 INTEGER                  NOT NULL,
    person_education                INTEGER                  NOT NULL,
    person_wealth_source            INTEGER                  NOT NULL,
    person_annual_income            DOUBLE PRECISION         NOT NULL,
    person_annual_income_currency   VARCHAR(16)              NOT NULL DEFAULT '',
    person_net_assets               DOUBLE PRECISION         NOT NULL,
    person_gender                   INTEGER                  NOT NULL,

    -- communication preferences
    preferred_communication         INTEGER                  NOT NULL,
    preferred_language              VARCHAR(16)              NOT NULL DEFAULT '',

    -- company; empty throughout for an individual
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

    -- contact; messengers and social_media are comma separated lists, stored as the API sends them
    email                           VARCHAR(64)              NOT NULL DEFAULT '',
    phone                           VARCHAR(32)              NOT NULL DEFAULT '',
    messengers                      VARCHAR(128)             NOT NULL DEFAULT '',
    social_media                    VARCHAR(128)             NOT NULL DEFAULT '',
    last_contact_date               TIMESTAMP WITH TIME ZONE NOT NULL,

    -- address
    address_country                 VARCHAR(32)              NOT NULL DEFAULT '',
    address_postcode                VARCHAR(16)              NOT NULL DEFAULT '',
    address_street                  VARCHAR(128)             NOT NULL DEFAULT '',
    address_state                   VARCHAR(64)              NOT NULL DEFAULT '',
    address_city                    VARCHAR(64)              NOT NULL DEFAULT '',

    -- metadata
    client_external_id              VARCHAR(64)              NOT NULL DEFAULT '',
    comment                         VARCHAR(256)             NOT NULL DEFAULT '',

    -- trading experience, in years; 0 also means not answered, as in the API
    experience_forex                INTEGER                  NOT NULL,
    experience_cfd                  INTEGER                  NOT NULL,
    experience_futures              INTEGER                  NOT NULL,
    experience_stocks               INTEGER                  NOT NULL,
    experience_crypto               INTEGER                  NOT NULL
);

-- index names lead with what they cover so 63-byte truncation cannot make two collide

-- a client's history; used by the projection's reconcile
CREATE INDEX IF NOT EXISTS idx_client_row_##client_events_table##
    ON ##client_events_table## (sender_id, client_id, id);

-- resync markers only, for the watermark lookup at the end of a resync
CREATE INDEX IF NOT EXISTS idx_markers_##client_events_table##
    ON ##client_events_table## (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
