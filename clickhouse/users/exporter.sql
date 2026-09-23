-- The role and user the trade server authenticates as.
--
-- The ClickHouse exporter does exactly one thing: INSERT INTO the ticks table over the native protocol. It
-- never reads, and a ClickHouse pipeline has no cursor table, so INSERT on the ticks table is the whole grant.
-- A right added here later is picked up on the next migration, because the changed content changes the MD5
-- this script is journalled under.
--
-- Applied once per --users entry with Access=exporter, for example:
--   --users "Access=exporter;User=trade_exporter;Password=passw0rd"

----break----

CREATE ROLE IF NOT EXISTS role_##database_name##_exporter ##cluster_name_suffix##;

----break----

-- ticks
GRANT ##cluster_name_suffix##
INSERT
ON ##database_name##.##ticks_table## TO role_##database_name##_exporter;

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
