-- A read-only role for reporting and support access to the exported ticks.
--
-- Applied once per --users entry with Access=ro, for example:
--   --users "Access=ro;User=trade_reader;Password=passw0rd"

----break----

CREATE ROLE IF NOT EXISTS role_##database_name##_ro ##cluster_name_suffix##;

----break----

-- the exporter role is created here only so that it can be revoked further down: ClickHouse refuses to revoke
-- a role that does not exist, and an account re-provisioned as read only must not keep the INSERT rights it
-- held as an exporter. On a deployment that has no exporter this leaves an empty, unassigned role behind.
CREATE ROLE IF NOT EXISTS role_##database_name##_exporter ##cluster_name_suffix##;

----break----

-- ticks
GRANT ##cluster_name_suffix##
SELECT
ON ##database_name##.##ticks_table## TO role_##database_name##_ro;

----break----

CREATE USER IF NOT EXISTS ##user_name## ##cluster_name_suffix##
IDENTIFIED WITH sha256_password BY '##user_password##'
DEFAULT ROLE role_##database_name##_ro;

----break----

-- keeps the password in sync when it is rotated
ALTER USER ##user_name## ##cluster_name_suffix##
IDENTIFIED WITH sha256_password BY '##user_password##';

----break----

-- both roles are revoked before the read-only one is granted back, so an account that previously held
-- Access=exporter converges on read-only access instead of keeping its INSERT rights
REVOKE ##cluster_name_suffix## role_##database_name##_exporter FROM ##user_name##;

----break----

REVOKE ##cluster_name_suffix## role_##database_name##_ro FROM ##user_name##;

----break----

GRANT ##cluster_name_suffix## role_##database_name##_ro TO ##user_name##;

----break----

ALTER USER ##user_name## ##cluster_name_suffix##
DEFAULT ROLE role_##database_name##_ro;
