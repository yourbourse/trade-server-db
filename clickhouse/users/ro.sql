-- A read-only role for reporting and support access to the exported data.
--
-- Applied once per --users entry with Access=ro, for example:
--   --users "Access=ro;User=trade_reader;Password=passw0rd"

----break----

CREATE ROLE IF NOT EXISTS role_##database_name##_ro ##cluster_name_suffix##;

----break----

-- trades
GRANT ##cluster_name_suffix##
SELECT
ON ##database_name##.##trades_table## TO role_##database_name##_ro;

----break----

-- export cursor
GRANT ##cluster_name_suffix##
SELECT
ON ##database_name##.##cursor_table## TO role_##database_name##_ro;

----break----

CREATE USER IF NOT EXISTS ##user_name## ##cluster_name_suffix##
IDENTIFIED WITH sha256_password BY '##user_password##'
DEFAULT ROLE role_##database_name##_ro;

----break----

-- keeps the password in sync when it is rotated
ALTER USER ##user_name## ##cluster_name_suffix##
IDENTIFIED WITH sha256_password BY '##user_password##';

----break----

REVOKE ##cluster_name_suffix## role_##database_name##_ro FROM ##user_name##;

----break----

GRANT ##cluster_name_suffix## role_##database_name##_ro TO ##user_name##;

----break----

ALTER USER ##user_name## ##cluster_name_suffix##
DEFAULT ROLE role_##database_name##_ro;
