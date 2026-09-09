-- Maintains ##trading_accounts_table## from ##trading_account_events_table##.
--
-- Re-runnable, and it lives here rather than in an incremental script for the reason every view does: this file
-- is always the current definition, it re-runs whenever its content changes, and --force re-applies it when the
-- effect was lost without the content changing - someone dropped the trigger, or edited the derived table by
-- hand. A trigger maintained table is an incrementally maintained materialized view, which is what this phase
-- is for.
--
-- Three parts, in this order and for that reason:
--
--   1. the projection function
--   2. the trigger, dropped and recreated so the file converges on one definition
--   3. a reconcile pass over whatever the log still holds
--
-- The reconcile is not belt and braces. Creating the trigger takes a lock on the log table that conflicts with
-- the exporter's INSERT, and the migrator runs every PostgreSQL script inside one transaction, so from part 2
-- onwards the exporter is held off and part 3 sees a table nothing else is writing to. That closes the window
-- between the log table existing (created in the folder root, phase 2) and this trigger existing (phase 3), in
-- which a running trade server could otherwise have exported events nothing would ever have projected. It also
-- seeds a deployment whose log already holds history, and it is the manual repair path.
--
-- What the reconcile cannot do: recover an account whose last event has aged out of the retention window, or
-- notice a deletion whose event has aged out. A resync interrupted by a migration is finished by the next
-- resync, not by this. Where it has to guess it errs towards leaving an account out rather than putting one
-- back: a missing live account is corrected by its next change, while a resurrected deleted account would sit
-- there looking live until somebody noticed. The authoritative repair for either is a resync from the trade
-- server, which is where the truth about the live set sits.
--
-- The event types are 0 account state, 1 deletion, 2 resync started, 3 resync finished. They have to match the
-- trade server's enum; ##trading_account_events_table## documents them alongside the column.

CREATE OR REPLACE FUNCTION project_##trading_accounts_table##()
    RETURNS trigger
    LANGUAGE plpgsql
AS
$$
BEGIN
    CASE NEW.event_type

        -- Account state. Create or replace the account's row and stamp it with the event that wrote it.
        --
        -- Last write wins, with no comparison against the stored version. That is deliberate twice over: the
        -- export delivers a sender's events in order, so the last to arrive is the newest by construction; and
        -- a resync re-announces an unchanged account at the version it already had, so a version guard would
        -- skip the row, leave it stamped with a pre-resync event, and the sweep below would then delete a live
        -- account. Comparing versions here would also hide an upstream ordering bug rather than surface it.
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

        -- Deletion. A deletion for an account with no row deletes nothing, which is the wanted behaviour.
        WHEN 1 THEN
            DELETE
            FROM ##trading_accounts_table##
            WHERE sender_id = NEW.sender_id
              AND trading_account_id = NEW.trading_account_id;

        -- Resync started. Nothing to do, deliberately - not an oversight.
        --
        -- Clearing the sender's rows here is the obvious reading of "discard everything previously known", and
        -- it is wrong for a table. An in-process cache can discard and rebuild safely because it gates its own
        -- reads until the rebuild finishes; this table has no such gate, its readers are other people's
        -- reporting queries, and a resync may span many transactions - so clearing here would publish an empty
        -- table for the whole resync, on every trade server startup that runs one. The rows are removed at the
        -- end marker instead, by which point their replacements have arrived.
        --
        -- Nor is there any bookkeeping to write. This row is itself the record that a resync began, and its id
        -- is the watermark the end marker reads back out of the log - so a start marker's only job is to exist.
        -- A second start marker before the first resync finished needs no handling either: the end marker takes
        -- the newest one, so the later resync supersedes the earlier one for free.
        WHEN 2 THEN
            NULL;

        -- Resync finished. Sweep: whatever the resync did not re-announce is gone.
        --
        -- The watermark is the sender's newest start marker, read straight from the log - idx_markers_ on
        -- ##trading_account_events_table## is the partial index that makes that a two-row lookup. An account
        -- re-announced during the resync, created during it or modified during it carries a stamp newer than
        -- the watermark and survives; everything else predates the resync and was therefore not in the live set
        -- the sender enumerated.
        --
        -- An end marker with no start marker before it leaves the subquery NULL, the comparison NULL, and
        -- nothing deleted - so a stray end marker cannot empty the table.
        --
        -- marker.id < NEW.id matters and is not belt and braces. An AFTER ROW trigger fires once the whole
        -- statement has landed, so by the time this runs every row of the batch is already in the table -
        -- including a later resync's start marker, if the batch happened to carry one. Without the bound this
        -- would then sweep against that later watermark and delete everything the resync it belongs to had just
        -- re-announced. The bound makes it "the newest start marker before this end marker", which is right
        -- whatever else the batch contains.
        WHEN 3 THEN
            DELETE
            FROM ##trading_accounts_table##
            WHERE sender_id = NEW.sender_id
              AND source_event_row_id < (SELECT max(marker.id)
                                         FROM ##trading_account_events_table## marker
                                         WHERE marker.sender_id = NEW.sender_id
                                           AND marker.event_type = 2
                                           AND marker.id < NEW.id);

        -- An event type this projection does not know: recorded in the log, left unprojected.
        --
        -- The ELSE branch is required, not stylistic. A plpgsql CASE with no matching branch and no ELSE raises
        -- CASE_NOT_FOUND, which would abort the exporting COPY and stall the export of every deployment that
        -- had not yet migrated to know the new event type. The log keeps the row either way, so nothing is
        -- lost; finding such a row is a monitoring query, not a write path concern:
        --
        --   SELECT DISTINCT event_type FROM ##trading_account_events_table##;
        ELSE
            NULL;

    END CASE;

    -- an AFTER trigger's return value is discarded
    RETURN NULL;
END;
$$;

-- DROP then CREATE rather than CREATE OR REPLACE TRIGGER: both take a lock that blocks the exporter and both
-- are atomic inside this script's single transaction, so the only difference between them is that
-- CREATE OR REPLACE TRIGGER needs PostgreSQL 14 and this does not. Nothing here states a minimum version, and
-- syntax sugar is not a reason to introduce one.
DROP TRIGGER IF EXISTS trg_project_##trading_accounts_table## ON ##trading_account_events_table##;

-- what the trigger does comes before the table name, as with the index names: PostgreSQL truncates an
-- identifier to 63 bytes, and a function lives in the schema rather than on a table, so a database carrying two
-- sets of these tables under different names would otherwise have them fight over one function.
CREATE TRIGGER trg_project_##trading_accounts_table##
    AFTER INSERT
    ON ##trading_account_events_table##
    FOR EACH ROW
EXECUTE FUNCTION project_##trading_accounts_table##();

-- Reconcile: apply, per account, the newest retained event that the derived row does not already reflect.
--
-- The newest event is taken first and its type inspected second. Filtering to state carrying events before
-- picking the newest would resurrect a deleted account from its last state event, which is why the subquery
-- keeps both types and the outer condition chooses. Markers are excluded because they are not about an account.
--
-- COALESCE to -1 covers an account with no derived row at all, whose every retained event is therefore newer
-- than what the table reflects. On a fresh install the log is empty and both statements do nothing.
WITH latest AS (SELECT DISTINCT ON (sender_id, trading_account_id) *
                FROM ##trading_account_events_table##
                WHERE event_type IN (0, 1)
                ORDER BY sender_id, trading_account_id, id DESC),
     -- Each sender's watermark from the most recent resync that actually finished: its newest start marker
     -- that has an end marker after it. A resync still in flight is skipped over, so an account it has not
     -- reached yet keeps reconciling from its pre-resync events while the previous completed resync goes on
     -- guarding against resurrection.
     --
     -- Aggregated once per sender here rather than looked up per account: the markers are a handful of rows
     -- behind idx_markers_, but ##trading_accounts_table## can hold a hundred thousand.
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
  -- >= rather than >, so the newest retained state event is replayed even when the row already claims to be
  -- stamped with it.
  --
  -- Nothing edits these tables by hand - SPEC.md assumes that - so the case this covers is our own: a row this
  -- projection wrote wrongly. The function copies fourteen columns twice, in the VALUES list and again in the
  -- ON CONFLICT SET list, and a mismapping there produces rows that are correctly stamped and wrong. Fixing the
  -- function and re-applying this file is the documented response to that, and a strict > would make it a no-op
  -- for every row the broken version had already written - it would compare each event against itself and skip.
  -- Two defects in this projection reached review already; the third should be repairable in one command.
  --
  -- The cost is that a re-application rewrites every account rather than only the stale ones: measured at 1.9 s
  -- and a transient doubling of the table for 100k accounts, on an operation that only runs on --force or when
  -- this file's content changes.
  AND l.id >= COALESCE((SELECT a.source_event_row_id
                        FROM ##trading_accounts_table## a
                        WHERE a.sender_id = l.sender_id
                          AND a.trading_account_id = l.trading_account_id), -1)
  -- and the account was not already swept by a resync that finished.
  --
  -- A sweep leaves nothing behind in the log: an account dropped from a resync is gone by virtue of its
  -- absence from the re-announcements, and absence is not an event. Without this condition the reconcile reads
  -- such an account's last pre-resync state event, finds no derived row, and brings it back from the dead.
  --
  -- The watermark is always available when it is needed, which is why this needs no stored state to lean on.
  -- Resurrection requires the account's last state event to still be retained, that event is older than the
  -- start marker that swept it, and ids rise with time - so any purge by age that still holds the event holds
  -- the marker too.
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
