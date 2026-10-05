-- Current state of every live client, one row per (sender_id, client_id).
--
-- Not written by the trade server: maintained from ##client_events_table## by the trigger in
-- views/clients_projection.sql, in the same transaction as the event.
-- client_id is the identifier ##trading_accounts_table## carries under that name, so the two tables join.
-- Same columns as a state carrying event, in the same order, minus the log's envelope.
-- No foreign key on source_event_row_id: events age out of retention long before the client does.

CREATE TABLE IF NOT EXISTS ##clients_table##
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
