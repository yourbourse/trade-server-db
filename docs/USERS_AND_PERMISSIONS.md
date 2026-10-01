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
| `exporter` | `users/exporter.sql` | The account the trade server authenticates as | PostgreSQL: `INSERT`, `SELECT` on the exported tables and `INSERT`, `SELECT`, `UPDATE` on the cursor. ClickHouse: `INSERT` on the ticks table |
| `ro` | `users/ro.sql` | Reporting and support access | `SELECT` on every table and view |

The exporter deliberately has **no DDL rights and no DELETE** — the export is append-only and never removes or
rewrites an exported record. Run migrations with a separate, more privileged account.

---

## How the `users/` phase works

Each `--users` entry names an access level, and the access level is the **file name** of the script to run:

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
- Omitting `--users` **fails the run** too. See below.

### `--users` is required on every run

A script in `users/` is only instantiated once per account that selects it, so a run without `--users` produces
no user script at all — the phase would report success having applied nothing. The accounts an earlier run
created would then stay frozen at the grants of that run, and every table a later migration adds would be
missing from them. That failure surfaces much later, as a permission error inside the trade server, with nothing
in the migration log pointing at the cause.

So the migrator refuses to start instead, as soon as the scripts in play implement any access level:

```
--users is required, on every migration and not only the first. The users folder implements exporter, ro, and a
run without --users applies none of them: an account created by an earlier run would keep the grants of that run
and be denied access to everything this migration adds. Pass the same accounts the deployment was migrated with
the first time, for example: --users "Access=exporter;User=trade_exporter;Password=<password>". The password is
re-applied on every run, so whatever is passed here becomes the account's password.
```

The check runs before the database is created and before any script executes, so a missing flag cannot leave a
half-provisioned deployment behind. Practical consequences:

- **Every migration needs the account passwords to hand.** Keep them in the secret store the migration command
  reads from rather than in someone's shell history — see the warning under [Rotation](#password-rotation).
- **`--dry-run` needs them too**, deliberately: a dry run is there to tell you whether the real command will
  work, so it validates the same thing.
- A custom `--scripts` folder set that carries **no** `users/` folder is exempt — there is no role to provision,
  so nothing is required. This is the only way to run without the flag.

The rule also means the first migration establishes the accounts every later migration has to repeat.

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
GRANT INSERT, SELECT ON TABLE ##trading_account_events_table## TO ##user_name##;
GRANT INSERT, SELECT, UPDATE, DELETE ON TABLE ##trading_accounts_table## TO ##user_name##;
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
| `trading_account_events` | `INSERT`, `SELECT` | `SELECT` |
| `trading_accounts` | `INSERT`, `SELECT`, `UPDATE`, `DELETE` | `SELECT` |
| `export_cursor` | `INSERT`, `SELECT`, `UPDATE` | `SELECT` |
| `trades_history` (view) | — | `SELECT` |
| `trading_accounts_resync` (view) | — | `SELECT` |
| `cash_transfers` (view) | — | `SELECT` |

`UPDATE` is granted on the cursor table alone: it is the only row the export ever rewrites. An exported record is
never updated or deleted.

The two views are granted to `ro` only — the exporter writes rows, it does not read reports. A view carries its
own privileges, so `SELECT` on `trades` does not reach through to a view over it; the grant has to name the view.
The `views/` phase runs before `users/`, so both exist by the time the grants are applied.

> **Note:** This is PostgreSQL only. ClickHouse holds no views, and its `ro` role is granted the ticks table
> alone. See [Schema Reference → Views](SCHEMA_REFERENCE.md#views).

The role is created only if missing, and the password is then set unconditionally — so re-running is safe and
also rotates the password.

> **Note:** Grants are per-table and by name, so a `--variables trades_table=…` override grants on the renamed
> table. A table added by a later migration is **not** granted automatically — add the `GRANT` to the `users/`
> scripts. That edit changes their content, so their MD5 changes and the phase re-runs on the next migration on
> its own, without `--force`. It re-runs *for the accounts that run passes*, which is why
> [`--users` is required every time](#--users-is-required-on-every-run).

---

## ClickHouse

ClickHouse separates roles from users, so the script creates a role named after the database, grants on it, then
creates the user and assigns the role. The only table on ClickHouse is the ticks table, created by
`clickhouse/20260922_01_create_ticks.sql` in the incremental phase, before the grants are applied:

```sql
CREATE ROLE IF NOT EXISTS role_##database_name##_exporter ##cluster_name_suffix##;

GRANT ##cluster_name_suffix## INSERT ON ##database_name##.##ticks_table## TO role_##database_name##_exporter;

CREATE USER IF NOT EXISTS ##user_name## ##cluster_name_suffix##
IDENTIFIED WITH sha256_password BY '##user_password##'
DEFAULT ROLE role_##database_name##_exporter;
```

| Grant | exporter | ro |
|-------|----------|-----|
| `ticks` | `INSERT` | `SELECT` |

`INSERT` alone, because that is all the exporter does: a ClickHouse pipeline sends `INSERT INTO … VALUES` over
the native protocol and never reads, and it has no cursor table — it keeps its export position on disk. A right
added here later is picked up on the next migration, because the edit changes the MD5 the script is journalled
under.

| Detail | Why it is like that |
|--------|---------------------|
| Role name is derived: `role_<database>_exporter` | One ClickHouse instance often holds several deployments; the database name keeps the roles apart |
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
2. Use `##user_name##` and `##user_password##` for the account, and the table variables of the engine —
   `##trades_table##`, `##daily_statements_table##`, `##order_snapshots_table##`, `##position_snapshots_table##`
   and `##cursor_table##` on PostgreSQL, `##ticks_table##` on ClickHouse — plus the view variables
   `##trades_history_view##` and `##cash_transfers_view##` on PostgreSQL, and `##database_name##`.
3. Follow the existing pattern: create if missing, then set the password unconditionally, then grant. The script
   must be idempotent — it is re-runnable.
4. ClickHouse: one statement per `----break----`, and `##cluster_name_suffix##` on every statement.
5. Apply it with `--users "Access=<access>;User=…;Password=…"`.

---

## See also

[Configuration](CONFIGURATION.md) · [Security](SECURITY.md) · [Schema Reference](SCHEMA_REFERENCE.md) ·
[Writing Migrations](WRITING_MIGRATIONS.md)
