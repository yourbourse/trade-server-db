-- Maintains ##trading_accounts_table## from ##trading_account_events_table##; function, trigger and reconcile
-- share one transaction.
-- Event types: 0 state, 1 deletion, 2 resync started, 3 resync finished.
-- The column lists are guarded by the "copy every field" test.

CREATE OR REPLACE FUNCTION project_##trading_accounts_table##()
    RETURNS trigger
    LANGUAGE plpgsql
AS
$$
BEGIN
    CASE NEW.event_type

        -- Upsert, last write wins; a version guard would let the sweep delete re-announced accounts.
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

        WHEN 1 THEN
            DELETE
            FROM ##trading_accounts_table##
            WHERE sender_id = NEW.sender_id
              AND trading_account_id = NEW.trading_account_id;

        -- Nothing: this row's id is the end marker's watermark.
        WHEN 2 THEN
            NULL;

        -- Sweep rows older than this resync's start marker; marker.id < NEW.id skips the next resync's.
        WHEN 3 THEN
            DELETE
            FROM ##trading_accounts_table##
            WHERE sender_id = NEW.sender_id
              AND source_event_row_id < (SELECT max(marker.id)
                                         FROM ##trading_account_events_table## marker
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
DROP TRIGGER IF EXISTS trg_project_##trading_accounts_table## ON ##trading_account_events_table##;

CREATE TRIGGER trg_project_##trading_accounts_table##
    AFTER INSERT
    ON ##trading_account_events_table##
    FOR EACH ROW
EXECUTE FUNCTION project_##trading_accounts_table##();

-- Reconcile: replay each account's newest event the row does not reflect; newest first, so deletions stick.
WITH latest AS (SELECT DISTINCT ON (sender_id, trading_account_id) *
                FROM ##trading_account_events_table##
                WHERE event_type IN (0, 1)
                ORDER BY sender_id, trading_account_id, id DESC),
     -- per sender, the start marker of the newest finished resync
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
  -- >= also repairs rows the projection wrote wrongly
  AND l.id >= COALESCE((SELECT a.source_event_row_id
                        FROM ##trading_accounts_table## a
                        WHERE a.sender_id = l.sender_id
                          AND a.trading_account_id = l.trading_account_id), -1)
  -- not swept by a finished resync (a sweep leaves no event)
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

-- deleted: the newest event is a deletion the row does not reflect
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

-- swept: a finished resync skipped the row while the trigger was absent, and a sweep leaves no event
DELETE
FROM ##trading_accounts_table## a
    USING (SELECT started.sender_id, max(started.id) AS watermark
           FROM ##trading_account_events_table## started
           WHERE started.event_type = 2
             AND started.id < (SELECT max(finished.id)
                               FROM ##trading_account_events_table## finished
                               WHERE finished.sender_id = started.sender_id
                                 AND finished.event_type = 3)
           GROUP BY started.sender_id) w
WHERE w.sender_id = a.sender_id
  AND a.source_event_row_id < w.watermark;
