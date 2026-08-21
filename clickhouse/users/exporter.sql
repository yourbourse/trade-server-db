-- The role and user the trade server authenticates as.
--
-- Applied once per --users entry with Access=exporter, for example:
--   --users "Access=exporter;User=trade_exporter;Password=passw0rd"

----break----

CREATE ROLE IF NOT EXISTS role_##database_name##_exporter ##cluster_name_suffix##;

----break----

-- trades
GRANT ##cluster_name_suffix##
SELECT, INSERT
ON ##database_name##.##trades_table## TO role_##database_name##_exporter;

----break----

-- export cursor
GRANT ##cluster_name_suffix##
SELECT, INSERT, ALTER UPDATE
ON ##database_name##.##cursor_table## TO role_##database_name##_exporter;

----break----

CREATE USER IF NOT EXISTS ##user_name## ##cluster_name_suffix##
IDENTIFIED WITH sha256_password BY '##user_password##'
DEFAULT ROLE role_##database_name##_exporter;

----break----

-- keeps the password in sync when it is rotated
ALTER USER ##user_name## ##cluster_name_suffix##
IDENTIFIED WITH sha256_password BY '##user_password##';

----break----

REVOKE ##cluster_name_suffix## role_##database_name##_exporter FROM ##user_name##;

----break----

GRANT ##cluster_name_suffix## role_##database_name##_exporter TO ##user_name##;

----break----

ALTER USER ##user_name## ##cluster_name_suffix##
DEFAULT ROLE role_##database_name##_exporter;
