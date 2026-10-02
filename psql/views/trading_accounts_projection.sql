-- Maintains ##trading_accounts_table## from ##trading_account_events_table##: the projection function,
-- the trigger, and a reconcile pass over the log. All three stay in one file: it is one transaction, and the
-- trigger's lock keeps the exporter out while the reconcile runs.
--
-- Event types: 0 account state, 1 deletion, 2 resync started, 3 resync finished.

CREATE OR REPLACE FUNCTION project_##trading_accounts_table##()
    RETURNS trigger
    LANGUAGE plpgsql
AS
$$
BEGIN
    CASE NEW.event_type

        -- Account state: upsert, last write wins. No version guard - a resync re-announces an unchanged
        -- account at the same version, and skipping it would get it swept.
        WHEN 0 THEN
            INSERT INTO ##trading_accounts_table## (sender_id,
                                                    trading_account_id,
                                                    version,
                                                    group_id,
                                                    client_id,
                                                    leverage,
                                                    introducer,
                                                    external_account_id,
                                                    comment,
                                                    enabled,
                                                    allow_trading,
                                                    time_created,
                                                    time_modified,
                                                    time_password_last_changed,
                                                    source_event_row_id,
                                                    time_applied)
            VALUES (NEW.sender_id,
                    NEW.trading_account_id,
                    NEW.version,
                    NEW.group_id,
                    NEW.client_id,
                    NEW.leverage,
                    NEW.introducer,
                    NEW.external_account_id,
                    NEW.comment,
                    NEW.enabled,
                    NEW.allow_trading,
                    NEW.time_created,
                    NEW.time_modified,
                    NEW.time_password_last_changed,
                    NEW.id,
                    now())
            ON CONFLICT (sender_id, trading_account_id) DO UPDATE
                SET version                    = excluded.version,
                    group_id                   = excluded.group_id,
                    client_id                  = excluded.client_id,
                    leverage                   = excluded.leverage,
                    introducer                 = excluded.introducer,
                    external_account_id        = excluded.external_account_id,
                    comment                    = excluded.comment,
                    enabled                    = excluded.enabled,
                    allow_trading              = excluded.allow_trading,
                    time_created               = excluded.time_created,
                    time_modified              = excluded.time_modified,
                    time_password_last_changed = excluded.time_password_last_changed,
                    source_event_row_id        = excluded.source_event_row_id,
                    time_applied               = excluded.time_applied;

        -- Deletion.
        WHEN 1 THEN
            DELETE
            FROM ##trading_accounts_table##
            WHERE sender_id = NEW.sender_id
              AND trading_account_id = NEW.trading_account_id;

        -- Resync started: nothing to do. Clearing here would publish an empty table for the whole resync;
        -- the end marker sweeps instead, using this row's id as the watermark.
        WHEN 2 THEN
            NULL;

        -- Resync finished: delete whatever the resync did not re-announce, i.e. rows stamped before the
        -- sender's newest start marker. marker.id < NEW.id because an AFTER ROW trigger already sees the whole
        -- batch, which may contain a later start marker. No start marker -> NULL -> nothing deleted.
        WHEN 3 THEN
            DELETE
            FROM ##trading_accounts_table##
            WHERE sender_id = NEW.sender_id
              AND source_event_row_id < (SELECT max(marker.id)
                                         FROM ##trading_account_events_table## marker
                                         WHERE marker.sender_id = NEW.sender_id
                                           AND marker.event_type = 2
                                           AND marker.id < NEW.id);

        -- Unknown event type: kept in the log, not projected. ELSE is required - without it plpgsql raises
        -- CASE_NOT_FOUND and the export stalls.
        ELSE
            NULL;

    END CASE;

    -- an AFTER trigger's return value is discarded
    RETURN NULL;
END;
$$;

-- DROP + CREATE rather than CREATE OR REPLACE TRIGGER, which needs PostgreSQL 14
DROP TRIGGER IF EXISTS trg_project_##trading_accounts_table## ON ##trading_account_events_table##;

-- name leads with what it does, so 63-byte truncation cannot make two collide
CREATE TRIGGER trg_project_##trading_accounts_table##
    AFTER INSERT
    ON ##trading_account_events_table##
    FOR EACH ROW
EXECUTE FUNCTION project_##trading_accounts_table##();

-- Reconcile: apply, per account, the newest retained event the derived row does not reflect yet.
-- The newest event is picked before its type is checked, so a deleted account is not resurrected.
WITH latest AS (SELECT DISTINCT ON (sender_id, trading_account_id) *
                FROM ##trading_account_events_table##
                WHERE event_type IN (0, 1)
                ORDER BY sender_id, trading_account_id, id DESC),
     -- each sender's watermark from the newest resync that finished
     completed_resync AS (SELECT started.sender_id, max(started.id) AS watermark
                          FROM ##trading_account_events_table## started
                          WHERE started.event_type = 2
                            AND started.id < (SELECT max(finished.id)
                                              FROM ##trading_account_events_table## finished
                                              WHERE finished.sender_id = started.sender_id
                                                AND finished.event_type = 3)
                          GROUP BY started.sender_id)
INSERT
INTO ##trading_accounts_table## (sender_id,
                                 trading_account_id,
                                 version,
                                 group_id,
                                 client_id,
                                 leverage,
                                 introducer,
                                 external_account_id,
                                 comment,
                                 enabled,
                                 allow_trading,
                                 time_created,
                                 time_modified,
                                 time_password_last_changed,
                                 source_event_row_id,
                                 time_applied)
SELECT l.sender_id,
       l.trading_account_id,
       l.version,
       l.group_id,
       l.client_id,
       l.leverage,
       l.introducer,
       l.external_account_id,
       l.comment,
       l.enabled,
       l.allow_trading,
       l.time_created,
       l.time_modified,
       l.time_password_last_changed,
       l.id,
       now()
FROM latest l
         LEFT JOIN completed_resync w ON w.sender_id = l.sender_id
WHERE l.event_type = 0
  -- >= so that re-applying this file also repairs rows the projection itself wrote wrongly
  AND l.id >= COALESCE((SELECT a.source_event_row_id
                        FROM ##trading_accounts_table## a
                        WHERE a.sender_id = l.sender_id
                          AND a.trading_account_id = l.trading_account_id), -1)
  -- and not already swept by a completed resync (a sweep leaves no event, so it would come back)
  AND l.id > COALESCE(w.watermark, -1)
ON CONFLICT (sender_id, trading_account_id) DO UPDATE
    SET version                    = excluded.version,
        group_id                   = excluded.group_id,
        client_id                  = excluded.client_id,
        leverage                   = excluded.leverage,
        introducer                 = excluded.introducer,
        external_account_id        = excluded.external_account_id,
        comment                    = excluded.comment,
        enabled                    = excluded.enabled,
        allow_trading              = excluded.allow_trading,
        time_created               = excluded.time_created,
        time_modified              = excluded.time_modified,
        time_password_last_changed = excluded.time_password_last_changed,
        source_event_row_id        = excluded.source_event_row_id,
        time_applied               = excluded.time_applied;

-- the other half: an account whose newest retained event is a deletion the derived row does not reflect yet
DELETE
FROM ##trading_accounts_table## a
    USING (SELECT DISTINCT ON (sender_id, trading_account_id) sender_id, trading_account_id, id, event_type
           FROM ##trading_account_events_table##
           WHERE event_type IN (0, 1)
           ORDER BY sender_id, trading_account_id, id DESC) l
WHERE l.sender_id = a.sender_id
  AND l.trading_account_id = a.trading_account_id
  AND l.event_type = 1
  AND l.id > a.source_event_row_id;
