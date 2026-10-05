-- A read-only role for reporting and support access to the exported data.
--
-- Applied once per --users entry with Access=ro, for example:
--   --users "Access=ro;User=trade_reader;Password=passw0rd"

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

-- the script has to converge on read-only access, so anything the account may already hold - INSERT and UPDATE
-- from a previous run with Access=exporter, say - is dropped before SELECT is granted back. Revoking a
-- privilege that was never granted is a no-op, so this is safe on a fresh role.
REVOKE ALL PRIVILEGES ON TABLE ##trades_table## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##daily_statements_table## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##order_snapshots_table## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##position_snapshots_table## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##trading_account_events_table## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##trading_accounts_table## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##trading_accounts_resync_view## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##client_events_table## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##clients_table## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##clients_resync_view## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##cursor_table## FROM ##user_name##;

-- a view carries its own privileges, so SELECT on the trades table does not reach through to them. The views
-- phase runs before this one, so both exist by the time they are named here.
REVOKE ALL PRIVILEGES ON TABLE ##trades_history_view## FROM ##user_name##;

REVOKE ALL PRIVILEGES ON TABLE ##cash_transfers_view## FROM ##user_name##;

GRANT SELECT ON TABLE ##trades_table## TO ##user_name##;

GRANT SELECT ON TABLE ##daily_statements_table## TO ##user_name##;

GRANT SELECT ON TABLE ##order_snapshots_table## TO ##user_name##;

GRANT SELECT ON TABLE ##position_snapshots_table## TO ##user_name##;

GRANT SELECT ON TABLE ##trading_account_events_table## TO ##user_name##;

GRANT SELECT ON TABLE ##trading_accounts_table## TO ##user_name##;

GRANT SELECT ON TABLE ##trading_accounts_resync_view## TO ##user_name##;

GRANT SELECT ON TABLE ##client_events_table## TO ##user_name##;

GRANT SELECT ON TABLE ##clients_table## TO ##user_name##;

GRANT SELECT ON TABLE ##clients_resync_view## TO ##user_name##;

GRANT SELECT ON TABLE ##cursor_table## TO ##user_name##;

GRANT SELECT ON TABLE ##trades_history_view## TO ##user_name##;

GRANT SELECT ON TABLE ##cash_transfers_view## TO ##user_name##;
