-- Maintains ##clients_table## from ##client_events_table##; function, trigger and reconcile share one transaction.
-- Event types: 0 state, 1 deletion, 2 resync started, 3 resync finished.
-- The column lists are guarded by the "copy every field" test.

CREATE OR REPLACE FUNCTION project_##clients_table##()
    RETURNS trigger
    LANGUAGE plpgsql
AS
$$
BEGIN
    CASE NEW.event_type

        -- Upsert, last write wins; a version guard would let the sweep delete re-announced clients.
        WHEN 0 THEN
            INSERT INTO ##clients_table## (sender_id,
                                           client_id,
                                           version,
                                           client_type,
                                           client_status,
                                           assigned_manager,
                                           time_created,
                                           time_modified,
                                           compliance_approved_by,
                                           compliance_client_category,
                                           compliance_date_approval,
                                           compliance_date_termination,
                                           lead_campaign,
                                           lead_source,
                                           introducer,
                                           person_title,
                                           person_first_name,
                                           person_middle_name,
                                           person_last_name,
                                           person_birth_date,
                                           person_citizenship,
                                           person_tax_id,
                                           person_document_type,
                                           person_document_number,
                                           person_document_date,
                                           person_document_expiration_date,
                                           person_document_extra,
                                           person_employment,
                                           person_industry,
                                           person_education,
                                           person_wealth_source,
                                           person_annual_income,
                                           person_annual_income_currency,
                                           person_net_assets,
                                           person_gender,
                                           preferred_communication,
                                           preferred_language,
                                           company_name,
                                           company_registration_number,
                                           company_registration_date,
                                           company_registration_authority,
                                           company_vat,
                                           company_lei,
                                           company_license_number,
                                           company_license_authority,
                                           company_country,
                                           company_registration_address,
                                           company_website,
                                           email,
                                           phone,
                                           messengers,
                                           social_media,
                                           last_contact_date,
                                           address_country,
                                           address_postcode,
                                           address_street,
                                           address_state,
                                           address_city,
                                           client_external_id,
                                           comment,
                                           experience_forex,
                                           experience_cfd,
                                           experience_futures,
                                           experience_stocks,
                                           experience_crypto,
                                           tags,
                                           source_event_row_id,
                                           time_applied)
            VALUES (NEW.sender_id,
                    NEW.client_id,
                    NEW.version,
                    NEW.client_type,
                    NEW.client_status,
                    NEW.assigned_manager,
                    NEW.time_created,
                    NEW.time_modified,
                    NEW.compliance_approved_by,
                    NEW.compliance_client_category,
                    NEW.compliance_date_approval,
                    NEW.compliance_date_termination,
                    NEW.lead_campaign,
                    NEW.lead_source,
                    NEW.introducer,
                    NEW.person_title,
                    NEW.person_first_name,
                    NEW.person_middle_name,
                    NEW.person_last_name,
                    NEW.person_birth_date,
                    NEW.person_citizenship,
                    NEW.person_tax_id,
                    NEW.person_document_type,
                    NEW.person_document_number,
                    NEW.person_document_date,
                    NEW.person_document_expiration_date,
                    NEW.person_document_extra,
                    NEW.person_employment,
                    NEW.person_industry,
                    NEW.person_education,
                    NEW.person_wealth_source,
                    NEW.person_annual_income,
                    NEW.person_annual_income_currency,
                    NEW.person_net_assets,
                    NEW.person_gender,
                    NEW.preferred_communication,
                    NEW.preferred_language,
                    NEW.company_name,
                    NEW.company_registration_number,
                    NEW.company_registration_date,
                    NEW.company_registration_authority,
                    NEW.company_vat,
                    NEW.company_lei,
                    NEW.company_license_number,
                    NEW.company_license_authority,
                    NEW.company_country,
                    NEW.company_registration_address,
                    NEW.company_website,
                    NEW.email,
                    NEW.phone,
                    NEW.messengers,
                    NEW.social_media,
                    NEW.last_contact_date,
                    NEW.address_country,
                    NEW.address_postcode,
                    NEW.address_street,
                    NEW.address_state,
                    NEW.address_city,
                    NEW.client_external_id,
                    NEW.comment,
                    NEW.experience_forex,
                    NEW.experience_cfd,
                    NEW.experience_futures,
                    NEW.experience_stocks,
                    NEW.experience_crypto,
                    NEW.tags,
                    NEW.id,
                    now())
            ON CONFLICT (sender_id, client_id) DO UPDATE
                SET version                         = excluded.version,
                    client_type                     = excluded.client_type,
                    client_status                   = excluded.client_status,
                    assigned_manager                = excluded.assigned_manager,
                    time_created                    = excluded.time_created,
                    time_modified                   = excluded.time_modified,
                    compliance_approved_by          = excluded.compliance_approved_by,
                    compliance_client_category      = excluded.compliance_client_category,
                    compliance_date_approval        = excluded.compliance_date_approval,
                    compliance_date_termination     = excluded.compliance_date_termination,
                    lead_campaign                   = excluded.lead_campaign,
                    lead_source                     = excluded.lead_source,
                    introducer                      = excluded.introducer,
                    person_title                    = excluded.person_title,
                    person_first_name               = excluded.person_first_name,
                    person_middle_name              = excluded.person_middle_name,
                    person_last_name                = excluded.person_last_name,
                    person_birth_date               = excluded.person_birth_date,
                    person_citizenship              = excluded.person_citizenship,
                    person_tax_id                   = excluded.person_tax_id,
                    person_document_type            = excluded.person_document_type,
                    person_document_number          = excluded.person_document_number,
                    person_document_date            = excluded.person_document_date,
                    person_document_expiration_date = excluded.person_document_expiration_date,
                    person_document_extra           = excluded.person_document_extra,
                    person_employment               = excluded.person_employment,
                    person_industry                 = excluded.person_industry,
                    person_education                = excluded.person_education,
                    person_wealth_source            = excluded.person_wealth_source,
                    person_annual_income            = excluded.person_annual_income,
                    person_annual_income_currency   = excluded.person_annual_income_currency,
                    person_net_assets               = excluded.person_net_assets,
                    person_gender                   = excluded.person_gender,
                    preferred_communication         = excluded.preferred_communication,
                    preferred_language              = excluded.preferred_language,
                    company_name                    = excluded.company_name,
                    company_registration_number     = excluded.company_registration_number,
                    company_registration_date       = excluded.company_registration_date,
                    company_registration_authority  = excluded.company_registration_authority,
                    company_vat                     = excluded.company_vat,
                    company_lei                     = excluded.company_lei,
                    company_license_number          = excluded.company_license_number,
                    company_license_authority       = excluded.company_license_authority,
                    company_country                 = excluded.company_country,
                    company_registration_address    = excluded.company_registration_address,
                    company_website                 = excluded.company_website,
                    email                           = excluded.email,
                    phone                           = excluded.phone,
                    messengers                      = excluded.messengers,
                    social_media                    = excluded.social_media,
                    last_contact_date               = excluded.last_contact_date,
                    address_country                 = excluded.address_country,
                    address_postcode                = excluded.address_postcode,
                    address_street                  = excluded.address_street,
                    address_state                   = excluded.address_state,
                    address_city                    = excluded.address_city,
                    client_external_id              = excluded.client_external_id,
                    comment                         = excluded.comment,
                    experience_forex                = excluded.experience_forex,
                    experience_cfd                  = excluded.experience_cfd,
                    experience_futures              = excluded.experience_futures,
                    experience_stocks               = excluded.experience_stocks,
                    experience_crypto               = excluded.experience_crypto,
                    tags                            = excluded.tags,
                    source_event_row_id             = excluded.source_event_row_id,
                    time_applied                    = excluded.time_applied;

        WHEN 1 THEN
            DELETE
            FROM ##clients_table##
            WHERE sender_id = NEW.sender_id
              AND client_id = NEW.client_id;

        -- Nothing: this row's id is the end marker's watermark.
        WHEN 2 THEN
            NULL;

        -- Sweep rows older than this resync's start marker; marker.id < NEW.id skips the next resync's.
        WHEN 3 THEN
            DELETE
            FROM ##clients_table##
            WHERE sender_id = NEW.sender_id
              AND source_event_row_id < (SELECT max(marker.id)
                                         FROM ##client_events_table## marker
                                         WHERE marker.sender_id = NEW.sender_id
                                           AND marker.event_type = 2
                                           AND marker.id < NEW.id);

        -- Unknown type: stored, not projected. ELSE stops CASE_NOT_FOUND stalling the export.
        ELSE
            NULL;

    END CASE;

    RETURN NULL;
END;
$$;

-- CREATE OR REPLACE TRIGGER needs PostgreSQL 14
DROP TRIGGER IF EXISTS trg_project_##clients_table## ON ##client_events_table##;

CREATE TRIGGER trg_project_##clients_table##
    AFTER INSERT
    ON ##client_events_table##
    FOR EACH ROW
EXECUTE FUNCTION project_##clients_table##();

-- Reconcile: replay each client's newest event the row does not reflect; newest first, so deletions stick.
WITH latest AS (SELECT DISTINCT ON (sender_id, client_id) *
                FROM ##client_events_table##
                WHERE event_type IN (0, 1)
                ORDER BY sender_id, client_id, id DESC),
     -- per sender, the start marker of the newest finished resync
     completed_resync AS (SELECT started.sender_id, max(started.id) AS watermark
                          FROM ##client_events_table## started
                          WHERE started.event_type = 2
                            AND started.id < (SELECT max(finished.id)
                                              FROM ##client_events_table## finished
                                              WHERE finished.sender_id = started.sender_id
                                                AND finished.event_type = 3)
                          GROUP BY started.sender_id)
INSERT
INTO ##clients_table## (sender_id,
                        client_id,
                        version,
                        client_type,
                        client_status,
                        assigned_manager,
                        time_created,
                        time_modified,
                        compliance_approved_by,
                        compliance_client_category,
                        compliance_date_approval,
                        compliance_date_termination,
                        lead_campaign,
                        lead_source,
                        introducer,
                        person_title,
                        person_first_name,
                        person_middle_name,
                        person_last_name,
                        person_birth_date,
                        person_citizenship,
                        person_tax_id,
                        person_document_type,
                        person_document_number,
                        person_document_date,
                        person_document_expiration_date,
                        person_document_extra,
                        person_employment,
                        person_industry,
                        person_education,
                        person_wealth_source,
                        person_annual_income,
                        person_annual_income_currency,
                        person_net_assets,
                        person_gender,
                        preferred_communication,
                        preferred_language,
                        company_name,
                        company_registration_number,
                        company_registration_date,
                        company_registration_authority,
                        company_vat,
                        company_lei,
                        company_license_number,
                        company_license_authority,
                        company_country,
                        company_registration_address,
                        company_website,
                        email,
                        phone,
                        messengers,
                        social_media,
                        last_contact_date,
                        address_country,
                        address_postcode,
                        address_street,
                        address_state,
                        address_city,
                        client_external_id,
                        comment,
                        experience_forex,
                        experience_cfd,
                        experience_futures,
                        experience_stocks,
                        experience_crypto,
                        tags,
                        source_event_row_id,
                        time_applied)
SELECT l.sender_id,
       l.client_id,
       l.version,
       l.client_type,
       l.client_status,
       l.assigned_manager,
       l.time_created,
       l.time_modified,
       l.compliance_approved_by,
       l.compliance_client_category,
       l.compliance_date_approval,
       l.compliance_date_termination,
       l.lead_campaign,
       l.lead_source,
       l.introducer,
       l.person_title,
       l.person_first_name,
       l.person_middle_name,
       l.person_last_name,
       l.person_birth_date,
       l.person_citizenship,
       l.person_tax_id,
       l.person_document_type,
       l.person_document_number,
       l.person_document_date,
       l.person_document_expiration_date,
       l.person_document_extra,
       l.person_employment,
       l.person_industry,
       l.person_education,
       l.person_wealth_source,
       l.person_annual_income,
       l.person_annual_income_currency,
       l.person_net_assets,
       l.person_gender,
       l.preferred_communication,
       l.preferred_language,
       l.company_name,
       l.company_registration_number,
       l.company_registration_date,
       l.company_registration_authority,
       l.company_vat,
       l.company_lei,
       l.company_license_number,
       l.company_license_authority,
       l.company_country,
       l.company_registration_address,
       l.company_website,
       l.email,
       l.phone,
       l.messengers,
       l.social_media,
       l.last_contact_date,
       l.address_country,
       l.address_postcode,
       l.address_street,
       l.address_state,
       l.address_city,
       l.client_external_id,
       l.comment,
       l.experience_forex,
       l.experience_cfd,
       l.experience_futures,
       l.experience_stocks,
       l.experience_crypto,
       l.tags,
       l.id,
       now()
FROM latest l
         LEFT JOIN completed_resync w ON w.sender_id = l.sender_id
WHERE l.event_type = 0
  -- >= also repairs rows the projection wrote wrongly
  AND l.id >= COALESCE((SELECT c.source_event_row_id
                        FROM ##clients_table## c
                        WHERE c.sender_id = l.sender_id
                          AND c.client_id = l.client_id), -1)
  -- not swept by a finished resync (a sweep leaves no event)
  AND l.id > COALESCE(w.watermark, -1)
ON CONFLICT (sender_id, client_id) DO UPDATE
    SET version                         = excluded.version,
        client_type                     = excluded.client_type,
        client_status                   = excluded.client_status,
        assigned_manager                = excluded.assigned_manager,
        time_created                    = excluded.time_created,
        time_modified                   = excluded.time_modified,
        compliance_approved_by          = excluded.compliance_approved_by,
        compliance_client_category      = excluded.compliance_client_category,
        compliance_date_approval        = excluded.compliance_date_approval,
        compliance_date_termination     = excluded.compliance_date_termination,
        lead_campaign                   = excluded.lead_campaign,
        lead_source                     = excluded.lead_source,
        introducer                      = excluded.introducer,
        person_title                    = excluded.person_title,
        person_first_name               = excluded.person_first_name,
        person_middle_name              = excluded.person_middle_name,
        person_last_name                = excluded.person_last_name,
        person_birth_date               = excluded.person_birth_date,
        person_citizenship              = excluded.person_citizenship,
        person_tax_id                   = excluded.person_tax_id,
        person_document_type            = excluded.person_document_type,
        person_document_number          = excluded.person_document_number,
        person_document_date            = excluded.person_document_date,
        person_document_expiration_date = excluded.person_document_expiration_date,
        person_document_extra           = excluded.person_document_extra,
        person_employment               = excluded.person_employment,
        person_industry                 = excluded.person_industry,
        person_education                = excluded.person_education,
        person_wealth_source            = excluded.person_wealth_source,
        person_annual_income            = excluded.person_annual_income,
        person_annual_income_currency   = excluded.person_annual_income_currency,
        person_net_assets               = excluded.person_net_assets,
        person_gender                   = excluded.person_gender,
        preferred_communication         = excluded.preferred_communication,
        preferred_language              = excluded.preferred_language,
        company_name                    = excluded.company_name,
        company_registration_number     = excluded.company_registration_number,
        company_registration_date       = excluded.company_registration_date,
        company_registration_authority  = excluded.company_registration_authority,
        company_vat                     = excluded.company_vat,
        company_lei                     = excluded.company_lei,
        company_license_number          = excluded.company_license_number,
        company_license_authority       = excluded.company_license_authority,
        company_country                 = excluded.company_country,
        company_registration_address    = excluded.company_registration_address,
        company_website                 = excluded.company_website,
        email                           = excluded.email,
        phone                           = excluded.phone,
        messengers                      = excluded.messengers,
        social_media                    = excluded.social_media,
        last_contact_date               = excluded.last_contact_date,
        address_country                 = excluded.address_country,
        address_postcode                = excluded.address_postcode,
        address_street                  = excluded.address_street,
        address_state                   = excluded.address_state,
        address_city                    = excluded.address_city,
        client_external_id              = excluded.client_external_id,
        comment                         = excluded.comment,
        experience_forex                = excluded.experience_forex,
        experience_cfd                  = excluded.experience_cfd,
        experience_futures              = excluded.experience_futures,
        experience_stocks               = excluded.experience_stocks,
        experience_crypto               = excluded.experience_crypto,
        tags                            = excluded.tags,
        source_event_row_id             = excluded.source_event_row_id,
        time_applied                    = excluded.time_applied;

-- deleted: the newest event is a deletion the row does not reflect
DELETE
FROM ##clients_table## c
    USING (SELECT DISTINCT ON (sender_id, client_id) sender_id, client_id, id, event_type
           FROM ##client_events_table##
           WHERE event_type IN (0, 1)
           ORDER BY sender_id, client_id, id DESC) l
WHERE l.sender_id = c.sender_id
  AND l.client_id = c.client_id
  AND l.event_type = 1
  AND l.id > c.source_event_row_id;

-- swept: a finished resync skipped the row while the trigger was absent, and a sweep leaves no event
DELETE
FROM ##clients_table## c
    USING (SELECT started.sender_id, max(started.id) AS watermark
           FROM ##client_events_table## started
           WHERE started.event_type = 2
             AND started.id < (SELECT max(finished.id)
                               FROM ##client_events_table## finished
                               WHERE finished.sender_id = started.sender_id
                                 AND finished.event_type = 3)
           GROUP BY started.sender_id) w
WHERE w.sender_id = c.sender_id
  AND c.source_event_row_id < w.watermark;

-- 20261021_01 added tags with a default: NOT NULL needed it for existing rows, and the old trigger omits tags.
-- The function above writes tags, so the default goes - last, as its ACCESS EXCLUSIVE lock lasts until commit.
ALTER TABLE ##clients_table##
    ALTER COLUMN tags DROP DEFAULT;
