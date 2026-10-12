-- Maintains ##groups_table## from ##group_events_table##; function, trigger and reconcile share one transaction.
-- Event types: 0 state, 1 deletion, 2 resync started, 3 resync finished.
-- The column lists are guarded by the "copy every field" test.

CREATE OR REPLACE FUNCTION project_##groups_table##()
    RETURNS trigger
    LANGUAGE plpgsql
AS
$$
BEGIN
    CASE NEW.event_type

        -- Upsert, last write wins; a version guard would let the sweep delete re-announced groups.
        WHEN 0 THEN
            INSERT INTO ##groups_table## (sender_id,
                                          group_id,
                                          version,
                                          name,
                                          company,
                                          email_service_id,
                                          currency,
                                          currency_decimal_places,
                                          interest_rate,
                                          free_margin_mode,
                                          stopout_value_type,
                                          stopout,
                                          margin_call_value_type,
                                          margin_call,
                                          open_position_type,
                                          exchange_trading_mode,
                                          default_leverage,
                                          demo_deposit,
                                          demo_inactivity_period,
                                          max_trade_history_period,
                                          max_orders,
                                          max_positions,
                                          max_market_data_subscriptions,
                                          send_daily_reports,
                                          send_monthly_reports,
                                          symbol_group_profile_count,
                                          commission_profile_count,
                                          source_event_row_id,
                                          time_applied)
            VALUES (NEW.sender_id,
                    NEW.group_id,
                    NEW.version,
                    NEW.name,
                    NEW.company,
                    NEW.email_service_id,
                    NEW.currency,
                    NEW.currency_decimal_places,
                    NEW.interest_rate,
                    NEW.free_margin_mode,
                    NEW.stopout_value_type,
                    NEW.stopout,
                    NEW.margin_call_value_type,
                    NEW.margin_call,
                    NEW.open_position_type,
                    NEW.exchange_trading_mode,
                    NEW.default_leverage,
                    NEW.demo_deposit,
                    NEW.demo_inactivity_period,
                    NEW.max_trade_history_period,
                    NEW.max_orders,
                    NEW.max_positions,
                    NEW.max_market_data_subscriptions,
                    NEW.send_daily_reports,
                    NEW.send_monthly_reports,
                    NEW.symbol_group_profile_count,
                    NEW.commission_profile_count,
                    NEW.id,
                    now())
            ON CONFLICT (sender_id, group_id) DO UPDATE
                SET version                       = excluded.version,
                    name                          = excluded.name,
                    company                       = excluded.company,
                    email_service_id              = excluded.email_service_id,
                    currency                      = excluded.currency,
                    currency_decimal_places       = excluded.currency_decimal_places,
                    interest_rate                 = excluded.interest_rate,
                    free_margin_mode              = excluded.free_margin_mode,
                    stopout_value_type            = excluded.stopout_value_type,
                    stopout                       = excluded.stopout,
                    margin_call_value_type        = excluded.margin_call_value_type,
                    margin_call                   = excluded.margin_call,
                    open_position_type            = excluded.open_position_type,
                    exchange_trading_mode         = excluded.exchange_trading_mode,
                    default_leverage              = excluded.default_leverage,
                    demo_deposit                  = excluded.demo_deposit,
                    demo_inactivity_period        = excluded.demo_inactivity_period,
                    max_trade_history_period      = excluded.max_trade_history_period,
                    max_orders                    = excluded.max_orders,
                    max_positions                 = excluded.max_positions,
                    max_market_data_subscriptions = excluded.max_market_data_subscriptions,
                    send_daily_reports            = excluded.send_daily_reports,
                    send_monthly_reports          = excluded.send_monthly_reports,
                    symbol_group_profile_count    = excluded.symbol_group_profile_count,
                    commission_profile_count      = excluded.commission_profile_count,
                    source_event_row_id           = excluded.source_event_row_id,
                    time_applied                  = excluded.time_applied;

        WHEN 1 THEN
            DELETE
            FROM ##groups_table##
            WHERE sender_id = NEW.sender_id
              AND group_id = NEW.group_id;

        -- Nothing: this row's id is the end marker's watermark.
        WHEN 2 THEN
            NULL;

        -- Sweep rows older than this resync's start marker; marker.id < NEW.id skips the next resync's.
        WHEN 3 THEN
            DELETE
            FROM ##groups_table##
            WHERE sender_id = NEW.sender_id
              AND source_event_row_id < (SELECT max(marker.id)
                                         FROM ##group_events_table## marker
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
DROP TRIGGER IF EXISTS trg_project_##groups_table## ON ##group_events_table##;

CREATE TRIGGER trg_project_##groups_table##
    AFTER INSERT
    ON ##group_events_table##
    FOR EACH ROW
EXECUTE FUNCTION project_##groups_table##();

-- Reconcile: replay each group's newest event the row does not reflect; newest first, so deletions stick.
WITH latest AS (SELECT DISTINCT ON (sender_id, group_id) *
                FROM ##group_events_table##
                WHERE event_type IN (0, 1)
                ORDER BY sender_id, group_id, id DESC),
     -- per sender, the start marker of the newest finished resync
     completed_resync AS (SELECT started.sender_id, max(started.id) AS watermark
                          FROM ##group_events_table## started
                          WHERE started.event_type = 2
                            AND started.id < (SELECT max(finished.id)
                                              FROM ##group_events_table## finished
                                              WHERE finished.sender_id = started.sender_id
                                                AND finished.event_type = 3)
                          GROUP BY started.sender_id)
INSERT
INTO ##groups_table## (sender_id,
                       group_id,
                       version,
                       name,
                       company,
                       email_service_id,
                       currency,
                       currency_decimal_places,
                       interest_rate,
                       free_margin_mode,
                       stopout_value_type,
                       stopout,
                       margin_call_value_type,
                       margin_call,
                       open_position_type,
                       exchange_trading_mode,
                       default_leverage,
                       demo_deposit,
                       demo_inactivity_period,
                       max_trade_history_period,
                       max_orders,
                       max_positions,
                       max_market_data_subscriptions,
                       send_daily_reports,
                       send_monthly_reports,
                       symbol_group_profile_count,
                       commission_profile_count,
                       source_event_row_id,
                       time_applied)
SELECT l.sender_id,
       l.group_id,
       l.version,
       l.name,
       l.company,
       l.email_service_id,
       l.currency,
       l.currency_decimal_places,
       l.interest_rate,
       l.free_margin_mode,
       l.stopout_value_type,
       l.stopout,
       l.margin_call_value_type,
       l.margin_call,
       l.open_position_type,
       l.exchange_trading_mode,
       l.default_leverage,
       l.demo_deposit,
       l.demo_inactivity_period,
       l.max_trade_history_period,
       l.max_orders,
       l.max_positions,
       l.max_market_data_subscriptions,
       l.send_daily_reports,
       l.send_monthly_reports,
       l.symbol_group_profile_count,
       l.commission_profile_count,
       l.id,
       now()
FROM latest l
         LEFT JOIN completed_resync w ON w.sender_id = l.sender_id
WHERE l.event_type = 0
  -- >= also repairs rows the projection wrote wrongly
  AND l.id >= COALESCE((SELECT g.source_event_row_id
                        FROM ##groups_table## g
                        WHERE g.sender_id = l.sender_id
                          AND g.group_id = l.group_id), -1)
  -- not swept by a finished resync (a sweep leaves no event)
  AND l.id > COALESCE(w.watermark, -1)
ON CONFLICT (sender_id, group_id) DO UPDATE
    SET version                       = excluded.version,
        name                          = excluded.name,
        company                       = excluded.company,
        email_service_id              = excluded.email_service_id,
        currency                      = excluded.currency,
        currency_decimal_places       = excluded.currency_decimal_places,
        interest_rate                 = excluded.interest_rate,
        free_margin_mode              = excluded.free_margin_mode,
        stopout_value_type            = excluded.stopout_value_type,
        stopout                       = excluded.stopout,
        margin_call_value_type        = excluded.margin_call_value_type,
        margin_call                   = excluded.margin_call,
        open_position_type            = excluded.open_position_type,
        exchange_trading_mode         = excluded.exchange_trading_mode,
        default_leverage              = excluded.default_leverage,
        demo_deposit                  = excluded.demo_deposit,
        demo_inactivity_period        = excluded.demo_inactivity_period,
        max_trade_history_period      = excluded.max_trade_history_period,
        max_orders                    = excluded.max_orders,
        max_positions                 = excluded.max_positions,
        max_market_data_subscriptions = excluded.max_market_data_subscriptions,
        send_daily_reports            = excluded.send_daily_reports,
        send_monthly_reports          = excluded.send_monthly_reports,
        symbol_group_profile_count    = excluded.symbol_group_profile_count,
        commission_profile_count      = excluded.commission_profile_count,
        source_event_row_id           = excluded.source_event_row_id,
        time_applied                  = excluded.time_applied;

-- deleted: the newest event is a deletion the row does not reflect
DELETE
FROM ##groups_table## g
    USING (SELECT DISTINCT ON (sender_id, group_id) sender_id, group_id, id, event_type
           FROM ##group_events_table##
           WHERE event_type IN (0, 1)
           ORDER BY sender_id, group_id, id DESC) l
WHERE l.sender_id = g.sender_id
  AND l.group_id = g.group_id
  AND l.event_type = 1
  AND l.id > g.source_event_row_id;

-- swept: a finished resync skipped the row while the trigger was absent, and a sweep leaves no event
DELETE
FROM ##groups_table## g
    USING (SELECT started.sender_id, max(started.id) AS watermark
           FROM ##group_events_table## started
           WHERE started.event_type = 2
             AND started.id < (SELECT max(finished.id)
                               FROM ##group_events_table## finished
                               WHERE finished.sender_id = started.sender_id
                                 AND finished.event_type = 3)
           GROUP BY started.sender_id) w
WHERE w.sender_id = g.sender_id
  AND g.source_event_row_id < w.watermark;
