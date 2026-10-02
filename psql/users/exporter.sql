-- The role the trade server authenticates as.
--
-- Per the Export Setup guide the exporter needs INSERT and SELECT on every exported table, and INSERT, SELECT
-- and UPDATE on the cursor table - the cursor row is the only thing the export ever rewrites. Nothing else: an
-- exported record is never updated or deleted.
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

-- the projection trigger runs as the exporter, so it needs to write the derived table
GRANT INSERT, SELECT, UPDATE, DELETE ON TABLE ##trading_accounts_table## TO ##user_name##;

GRANT INSERT, SELECT, UPDATE ON TABLE ##cursor_table## TO ##user_name##;
