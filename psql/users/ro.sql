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

GRANT SELECT ON TABLE ##trades_table## TO ##user_name##;

GRANT SELECT ON TABLE ##cursor_table## TO ##user_name##;
