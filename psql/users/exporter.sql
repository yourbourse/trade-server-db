-- The role the trade server authenticates as.
--
-- Per the Export Setup guide the exporter needs INSERT and SELECT on every exported table, and INSERT, SELECT
-- and UPDATE on the cursor table - the cursor row is the only thing the export ever rewrites. An exported
-- record itself is never updated or deleted.
--
-- ##trading_accounts_table## is the exception, and it is not an exported record: it is derived from
-- ##trading_account_events_table## by a trigger, and that trigger runs as whoever inserted the event - the
-- exporter. So the exporter needs to write and delete rows in it, even though it never reads it itself. The
-- wider grant gives nothing away: a principal that can insert events can already empty the derived table
-- through the trigger, by writing a resync marker pair that re-announces nothing.
--
-- Applied once per --users entry with Access=exporter, for example:
--   --users "Access=exporter;User=trade_exporter;Password=passw0rd"

DO
$$
    BEGIN
        IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = '##user_name##') THEN
            CREATE ROLE ##user_name## LOGIN PASSWORD '##user_password##';
        END IF;
    END
$$;

-- keeps the password in sync when it is rotated
ALTER ROLE ##user_name## WITH LOGIN PASSWORD '##user_password##';

GRANT CONNECT ON DATABASE ##database_name## TO ##user_name##;

GRANT USAGE ON SCHEMA public TO ##user_name##;

GRANT INSERT, SELECT ON TABLE ##trades_table## TO ##user_name##;

GRANT INSERT, SELECT ON TABLE ##daily_statements_table## TO ##user_name##;

GRANT INSERT, SELECT ON TABLE ##order_snapshots_table## TO ##user_name##;

GRANT INSERT, SELECT ON TABLE ##position_snapshots_table## TO ##user_name##;

GRANT INSERT, SELECT ON TABLE ##trading_account_events_table## TO ##user_name##;

-- SELECT as well as the write privileges: a resync's end marker reads source_event_row_id off the rows it
-- deletes, and the watermark it compares them against off ##trading_account_events_table##
GRANT INSERT, SELECT, UPDATE, DELETE ON TABLE ##trading_accounts_table## TO ##user_name##;

GRANT INSERT, SELECT, UPDATE ON TABLE ##cursor_table## TO ##user_name##;
