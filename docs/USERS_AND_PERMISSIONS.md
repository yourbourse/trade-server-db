<div align="center">

# Users & Permissions

**The roles the export pipeline needs, and how the migrator provisions them**

[The two roles](#the-two-roles) · [How the phase works](#how-the-users-phase-works) · [PostgreSQL](#postgresql) · [ClickHouse](#clickhouse) · [Rotation](#password-rotation) · [Adding a role](#adding-a-role)

</div>

---

## The two roles

The *Export Setup* guide describes two accounts, and the `users/` phase provisions exactly those:

| Access level | Script | Purpose | Rights |
|--------------|--------|---------|--------|
| `exporter` | `users/exporter.sql` | The account the trade server authenticates as | `INSERT`, `SELECT` on the exported tables; `INSERT`, `SELECT`, `UPDATE` on the cursor |
| `ro` | `users/ro.sql` | Reporting and support access | `SELECT` on every table and view |

The exporter deliberately has **no DDL rights and no DELETE** — the export is append-only and never removes or
rewrites an exported record. Run migrations with a separate, more privileged account.

---

## How the `users/` phase works

Nothing is created unless you ask for it. Each `--users` entry names an access level, and the access level is
the **file name** of the script to run:

```bash
./migrate.sh --provider postgres --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=..." "Access=ro;User=trade_reader;Password=..."
```

| Entry key | Meaning | Validation |
|-----------|---------|------------|
| `Access` | Picks `users/<Access>.sql` | Must match a file, or the run fails |
| `User` | The role/user name | Must be a plain identifier |
| `Password` | The password | Escaped for the dialect, may contain almost anything — including `;` and `=` |

The mechanics:

- The script runs **once per matching entry**. Two `exporter` entries create two users from the same script.
- `##user_name##` and `##user_password##` are substituted per entry.
- The journal entry carries the user name, so each user gets its own row:
  `[psql/users] exporter.sql for trade_exporter::7f3c…`
- An `Access` value that matches no file **fails the run** before anything is applied, naming the account and
  listing the access levels that do exist.
- Omitting `--users` skips the phase entirely. It never revokes or drops anything.

> **Warning:** All three keys are required. A malformed entry fails the run with the expected format, with the
> password masked in the error. A password containing `=` or `;` is fine — the entry is split only at the
> semicolons that introduce `Access=`, `User=` or `Password=`, and only the first `=` of a pair separates key
> from value.

### Re-provisioning changes access

The scripts converge on the access level you ask for, rather than merely adding to it. Running an account that
previously held `Access=exporter` with `Access=ro` **revokes its write access**: PostgreSQL revokes all
privileges on every table before granting `SELECT` back, and ClickHouse revokes the exporter role before
granting the read-only one. The reverse direction needs no special handling — the exporter grants are a superset
of the read-only ones.

> **Note:** ClickHouse refuses to revoke a role that does not exist, so `clickhouse/users/ro.sql` creates the
> exporter role if missing purely so it can revoke it. On a deployment that never provisions an exporter this
> leaves an empty, unassigned role behind.

---

## PostgreSQL

`psql/users/exporter.sql` creates a login role, keeps its password current, and grants exactly what the export
needs:

```sql
DO
$$
    BEGIN
        IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = '##user_name##') THEN
            CREATE ROLE ##user_name## LOGIN PASSWORD '##user_password##';
        END IF;
    END
$$;

ALTER ROLE ##user_name## WITH LOGIN PASSWORD '##user_password##';

GRANT CONNECT ON DATABASE ##database_name## TO ##user_name##;
GRANT USAGE ON SCHEMA public TO ##user_name##;
GRANT INSERT, SELECT ON TABLE ##trades_table## TO ##user_name##;
GRANT INSERT, SELECT ON TABLE ##daily_statements_table## TO ##user_name##;
GRANT INSERT, SELECT ON TABLE ##order_snapshots_table## TO ##user_name##;
GRANT INSERT, SELECT ON TABLE ##position_snapshots_table## TO ##user_name##;
GRANT INSERT, SELECT, UPDATE ON TABLE ##cursor_table## TO ##user_name##;
```

| Grant | exporter | ro |
|-------|----------|-----|
| `CONNECT` on the database | yes | yes |
| `USAGE` on schema `public` | yes | yes |
| `trades` | `INSERT`, `SELECT` | `SELECT` |
| `daily_statements_export` | `INSERT`, `SELECT` | `SELECT` |
| `daily_statements_order_snapshots` | `INSERT`, `SELECT` | `SELECT` |
| `daily_statements_position_snapshots` | `INSERT`, `SELECT` | `SELECT` |
| `export_cursor` | `INSERT`, `SELECT`, `UPDATE` | `SELECT` |
| `trades_history` (view) | — | `SELECT` |
| `cash_transfers` (view) | — | `SELECT` |

`UPDATE` is granted on the cursor table alone: it is the only row the export ever rewrites. An exported record is
never updated or deleted.

The two views are granted to `ro` only — the exporter writes rows, it does not read reports. A view carries its
own privileges, so `SELECT` on `trades` does not reach through to a view over it; the grant has to name the view.
On PostgreSQL the `views/` phase runs before `users/`, so both exist by the time the grants are applied. On
ClickHouse the views are provisioned by the deployment rather than here, and the grant names them anyway — the
same by-name arrangement the exported tables already use.

The role is created only if missing, and the password is then set unconditionally — so re-running is safe and
also rotates the password.

> **Note:** Grants are per-table and by name, so a `--variables trades_table=…` override grants on the renamed
> table. A table added by a later migration is **not** granted automatically — add the `GRANT` to the `users/`
> scripts, and because that edit changes their content the phase re-runs on the next migration by itself.

---

## ClickHouse

ClickHouse separates roles from users, so the script creates a role named after the database, grants on it, then
creates the user and assigns the role. The tables themselves are **not** created here — a ClickHouse deployment
provisions the exported tables and the cursor itself, and ClickHouse happily grants on a table by name before it
exists:

```sql
CREATE ROLE IF NOT EXISTS role_##database_name##_exporter ##cluster_name_suffix##;

GRANT ##cluster_name_suffix## SELECT, INSERT ON ##database_name##.##trades_table## TO role_##database_name##_exporter;
GRANT ##cluster_name_suffix## SELECT, INSERT ON ##database_name##.##daily_statements_table## TO role_##database_name##_exporter;
GRANT ##cluster_name_suffix## SELECT, INSERT ON ##database_name##.##order_snapshots_table## TO role_##database_name##_exporter;
GRANT ##cluster_name_suffix## SELECT, INSERT ON ##database_name##.##position_snapshots_table## TO role_##database_name##_exporter;
GRANT ##cluster_name_suffix## SELECT, INSERT, ALTER UPDATE ON ##database_name##.##cursor_table## TO role_##database_name##_exporter;
-- ro additionally: SELECT on ##trades_history_view## and ##cash_transfers_view##

CREATE USER IF NOT EXISTS ##user_name## ##cluster_name_suffix##
IDENTIFIED WITH sha256_password BY '##user_password##'
DEFAULT ROLE role_##database_name##_exporter;
```

| Detail | Why it is like that |
|--------|---------------------|
| Role name is derived: `role_<database>_exporter` | One ClickHouse instance often holds several deployments; the database name keeps the roles apart |
| `ALTER UPDATE` instead of `UPDATE` | ClickHouse has no row `UPDATE`; the cursor is advanced with a mutation |
| `sha256_password` | The password is stored hashed, not in plain text |
| `REVOKE` then `GRANT` the role | Makes the assignment converge on exactly one role, so re-running cannot accumulate stale roles |
| `ALTER USER … DEFAULT ROLE` | Ensures the role is active on login, not merely available |
| Every statement carries `##cluster_name_suffix##` | Access control is per-node otherwise; on a cluster the user would exist on one host only |

The user's password is also re-applied on every run, same as PostgreSQL.

> **Note:** Local access management must be enabled on the server for these statements to work. The compose file
> sets `CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT: 1` for exactly this reason. On a managed ClickHouse the migration
> account needs `ACCESS MANAGEMENT` privileges.

---

## Password rotation

Both providers re-apply the password on every run, so rotation is a normal migration:

```bash
./migrate.sh --provider postgres --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=<new password>"
```

Because the new password changes the processed content of the script, its MD5 changes and the script re-runs
automatically. No `--force` needed.

Passwords are never bound as parameters — they sit inside a quoted literal in the script — so they are escaped
for the dialect they are written into: quotes doubled for PostgreSQL, backslash escapes for ClickHouse. A
password containing `'`, `\`, `;` or `--` is stored intact and still authenticates; there is an integration test
that logs in with one to prove it. Details in [Security](SECURITY.md).

> **Warning:** A password reaching the migrator on the command line is visible in the process list and your shell
> history. Prefer a secret store that writes it into the command from a variable your shell does not record, and
> remember the migrator masks it in its own output but cannot mask it in your terminal scrollback.

---

## Adding a role

The access level is just a file name, so a third role needs no code change:

1. Create `psql/users/<access>.sql` and `clickhouse/users/<access>.sql`.
2. Use `##user_name##` and `##user_password##` for the account, and the table variables — `##trades_table##`,
   `##daily_statements_table##`, `##order_snapshots_table##`, `##position_snapshots_table##`,
   `##cursor_table##`, `##trades_history_view##`, `##cash_transfers_view##` — plus `##database_name##` for the
   objects.
3. Follow the existing pattern: create if missing, then set the password unconditionally, then grant. The script
   must be idempotent — it is re-runnable.
4. ClickHouse: one statement per `----break----`, and `##cluster_name_suffix##` on every statement.
5. Apply it with `--users "Access=<access>;User=…;Password=…"`.

---

## See also

[Configuration](CONFIGURATION.md) · [Security](SECURITY.md) · [Schema Reference](SCHEMA_REFERENCE.md) ·
[Writing Migrations](WRITING_MIGRATIONS.md)
