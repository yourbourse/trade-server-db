# trade-server-db

Schema migrations for the databases the **trade server** exports trading data into.

The trade server writes trades and an export cursor to an external database — see the *Export Setup* guide for
how the export pipeline is configured. This repository owns the other half: creating and evolving the tables,
indexes and users that export needs.

Two providers are supported:

| Provider | Scripts | Driver | Journal |
|---|---|---|---|
| PostgreSQL | [`psql/`](psql) | Npgsql, via DbUp | `schema_versions` |
| ClickHouse | [`clickhouse/`](clickhouse) | `ClickHouse.Driver` (official, HTTP) | `schema_versions` |

Neither provider uses Entity Framework. Migrations are plain SQL files applied by
[DbUp](https://dbup.readthedocs.io/).

> **There is no rollback.** The migrator only rolls forward. A mistake is corrected by adding a new script, not
> by undoing an old one.

## Quick start

```bash
# PostgreSQL
./migrate.sh --provider postgres \
             --connection-string "Host=db.example.com;Port=5432;Database=trading;Username=postgres;Password=..."

# ClickHouse
./migrate.sh --provider clickhouse \
             --connection-string "Host=ch.example.com;Port=8123;Database=trading;Username=default;Password=..."

# or let the trade server's own configuration supply the connection and the table names
./migrate.sh --export-config /opt/trade-server/config/export_config.json
```

`migrate.sh` builds `src/Migrator` and runs it from the root of the repository, so the scripts in `psql/` and
`clickhouse/` are picked up as they are on disk. Every argument is passed straight through, so
`./migrate.sh --help` lists the full set of options.

## Command line

| Option | Description |
|---|---|
| `-p, --provider` | `clickhouse` or `postgres`. Optional when `--export-config` is given |
| `-l, --connection-string` | The database to migrate. Falls back to `$TSDB_CONNECTION_STRING`, then to `--export-config` |
| `-e, --export-config` | Path to the trade server `export_config.json`. Supplies the connection details and the table names |
| `-s, --scripts` | Script folders to apply. Defaults to a single folder named after the provider |
| `-r, --scripts-root` | The folder the script folders are resolved against. Defaults to the current directory, then to the folder holding the binary |
| `-c, --cluster` | ClickHouse only. Runs every statement `ON CLUSTER` and switches the engines to their `Replicated` variants |
| `-u, --users` | `Access=exporter;User=trade_exporter;Password=passw0rd`. Several values follow one flag, separated by spaces |
| `-v, --variables` | `key=value`, substituted into the scripts as `##key##`. Applied last, so it overrides the derived values. Several values follow one flag, separated by spaces |
| `-j, --journal-table` | The table recording applied scripts. Defaults to `schema_versions` |
| `-f, --force` | Re-applies every re-runnable script even when its content has not changed |
| `-n, --dry-run` | Reports the scripts that would be applied and touches nothing |

The migrator exits `0` on success and non-zero on the first failure. Passwords are masked in everything it
writes to the console, the log file and `logs/migrator.log`.

`--users` and `--variables` take a list, so repeating the flag is an error
(`Option 'u, users' is defined multiple times`). Put the values after a single flag instead:

```bash
--users "Access=exporter;User=trade_exporter;Password=passw0rd" "Access=ro;User=trade_reader;Password=passw0rd"
```

### Driving it from `export_config.json`

Pointing `--export-config` at the file the trade server already uses keeps the schema and the exporter in step:
the connection details, the trades table name (`table`) and the cursor table name (`cursor_table`) are all read
from it, and one migration runs per pipeline. `--provider` narrows it to the pipelines of a single target, and
`--connection-string` still wins if you need to migrate a different host than the one the server writes to.

## How the scripts are organised

Each provider folder holds the same five phases, applied in this order:

| Order | Folder | Re-runnable | Purpose |
|---|---|---|---|
| 1 | `pre/` | yes | Anything that has to exist before the tables. Optional, create it when you need it |
| 2 | *(the folder root)* | **no** | Incremental migrations. Applied once, in file name order, and never again |
| 3 | `views/` | yes | Views and materialized views |
| 4 | `users/` | yes | Roles and users. Applied once per matching `--users` entry |
| 5 | `post/` | yes | Everything that has to run after the schema, such as TTLs |

**Incremental** scripts (the folder root) are recorded in `schema_versions` under their file name and are never
applied twice. Name them `YYYYMMDD_NN_what_it_does.sql`.

**Re-runnable** scripts are recorded with the MD5 of their processed content appended to the name
(`[psql/views] trades_daily.sql::a1b2...`). Before a new entry is written the previous entry for that file is
deleted, so exactly one row per script survives and the script re-runs whenever its content changes — and only
then. `--force` ignores the hashes and re-applies all of them.

A script that fails aborts the run: later phases are not attempted and the exit code is non-zero.

### Template variables

Every script is preprocessed, replacing `##name##` with a value:

| Variable | Value |
|---|---|
| `##database_name##` | The database from the connection string |
| `##trades_table##` | `table` from the export configuration, otherwise `trades` |
| `##cursor_table##` | `cursor_table` from the export configuration, otherwise `export_cursor` |
| `##assembly_version##` | The version of the migrator that applied the script |
| `##user_name##`, `##user_password##` | The `users/` phase only, one pass per `--users` entry |
| `##cluster_name##`, `##cluster_name_suffix##` | ClickHouse only. Empty unless `--cluster` is given |
| `##merge_tree_engine##`, `##replacing_merge_tree_engine##` | ClickHouse only. `MergeTree`/`ReplacingMergeTree`, or the `Replicated*` variants when `--cluster` is given |

Anything else can be passed with `--variables key=value`.

A script containing a `##name##` that is not in this list and was not passed with `--variables` is **rejected
before it reaches the database**, naming the placeholder — otherwise the `##` travels through to the server and
comes back as a syntax error pointing at a character offset. If you want a literal table name, just write it;
`##trades_table##` and `##cursor_table##` are templated only because the trade server's export configuration
makes those two names configurable.

### Statement separators

ClickHouse accepts one statement per request, so ClickHouse scripts separate statements with a line of four or
more dashes:

```sql
----break----
```

PostgreSQL scripts do not need it — a whole file is sent as one command inside one transaction. Add a separator
only for a statement that must run on its own, such as `CREATE INDEX CONCURRENTLY`, which PostgreSQL refuses to
run inside a transaction block.

A chunk containing nothing but comments is dropped rather than sent to the server.

## Injection safety

Anything the migrator sends that is a **value** travels as a bound parameter — the journal lookups against
`information_schema` / `system.tables`, the journal insert, and the delete-by-prefix that clears the previous
entry of a re-runnable script (script names come from file names on disk).

Neither PostgreSQL nor ClickHouse accepts a bind parameter in place of an **identifier**, so `CREATE TABLE
<journal>`, `CREATE ROLE <user>`, `GRANT … TO <user>`, `create database <name>` and `ON CLUSTER` have to be
assembled as text. Those are guarded instead:

* `--journal-table`, `##database_name##`, `##trades_table##`, `##cursor_table##`, `##cluster_name##` and every
  `--users` name must match `^[A-Za-z_][A-Za-z0-9_]{0,62}$` — the intersection of what both databases accept as
  an unquoted identifier. Anything else fails the run before a statement is built.
* `##user_password##` sits inside a quoted literal in `users/*.sql` and so cannot be bound. It is escaped for
  the dialect it is written into: doubled quotes for PostgreSQL, backslash escapes for ClickHouse (backslash
  first, then quote). A password containing `'`, `\`, `;` or `--` is stored intact and still authenticates —
  there is an integration test that logs in with one to prove it.

Values passed with `--variables` are substituted verbatim and are **not** validated, because they legitimately
carry things that are not identifiers (engine names, TTL numbers, intervals). Treat that flag as trusted
operator input.

Worth being clear about the threat model: everything above is operator-supplied, and an operator running
migrations already holds DDL rights on the target. The guards exist so a password from a secret store that
happens to contain a quote fails loudly or works correctly, rather than silently producing the wrong SQL.

## Adding a migration

1. Create the file. Incremental: `psql/20260315_01_add_broker_id.sql`. Re-runnable: drop it in `views/`,
   `users/` or `post/`.
2. Use `##trades_table##` and `##cursor_table##` rather than hard-coded table names — they are configurable in
   the trade server's export configuration.
3. Check what it would do: `./migrate.sh --provider postgres --connection-string "..." --dry-run`.
4. Apply it, then run it a second time and confirm nothing is applied.
5. Mirror the change in the other provider's folder if it applies to both.

Never edit a script that has already been applied somewhere: an incremental script will not run again, and the
deployed database will silently differ from the file. Add a new one.

## Schema

Both providers carry the same two tables, matching the *Export Setup* guide.

`trades` is append-only. Delivery is at-least-once, so the same trade can arrive twice after a restart:

* **PostgreSQL** deduplicates with a unique index on `(sender_id, trade_id)`; the second insert is rejected.
* **ClickHouse** has no unique index, so the table is a `ReplacingMergeTree` sorted by `(sender_id, trade_id)`.
  Duplicates collapse when the parts merge — query with `FINAL` when they must not be visible before that.

The ClickHouse columns follow the guide's types: `BIGINT` → `Int64`, `SMALLINT` → `Int16`, `NUMERIC` →
`Decimal(38, 18)`, `VARCHAR` → `String`, `TIMESTAMP WITH TIME ZONE` → `DateTime64(6, 'UTC')`. The guide's
identity column has no equivalent and is left out — ClickHouse does not generate row ids, and the sorting key
already identifies a row.

> `Decimal` cannot hold `NaN`, and the guide states that non-trading records (Balance, Credit, Fee and the
> other accounting types) carry `0` or `NaN` in the fields that do not apply to them. An exporter that sends
> `NaN` gets `DECIMAL_OVERFLOW` and has to write `0` instead. Switch the `Decimal(38, 18)` columns to `Float64`
> if the writer cannot be changed.

`export_cursor` records how far the export has got. On PostgreSQL the cursor row and the batch of trades are
written in the same transaction, which is what makes the cursor authoritative. ClickHouse has no `UPDATE`, so
the exporter appends a new position and `ReplacingMergeTree` collapses the history, using
`last_written_position` as the version column so the highest position always wins.

The `users/` scripts provision the two roles the guide describes: `exporter` (INSERT and SELECT on trades,
INSERT, SELECT and UPDATE on the cursor) and `ro` (SELECT on both).

## Building and testing

```bash
dotnet build TradeServerDb.slnx

# unit tests
dotnet test src/Migrator.Tests/Migrator.Tests.csproj --filter "Category!=Integration"

# integration tests, against the containers in docker-compose.yml
docker compose up -d
ASPNETCORE_ENVIRONMENT=integration dotnet test src/Migrator.Tests/Migrator.Tests.csproj --filter "Category=Integration"
```

The integration tests provision a throwaway database per test, run the real migration twice and assert that the
second run applies nothing. Connection strings come from
[`appsettings.integration.json`](src/Migrator.Tests/appsettings.integration.json) and can be overridden with the
`ClickHouse__ConnectionString` and `Postgres__ConnectionString` environment variables.

`ClickHouse.Driver` 1.3.0 sends settings that older servers reject, so ClickHouse **24.x or newer** is required.

## Layout

```
clickhouse/            ClickHouse scripts: root = incremental, views/ users/ post/ = re-runnable
psql/                  PostgreSQL scripts, same layout
src/DbUp.ClickHouse/   DbUp provider for ClickHouse (connection manager, journal, executor)
src/DbUp.PSQL/         DbUp provider for PostgreSQL
src/Migrator/          The CLI
src/Migrator.Tests/    Unit and integration tests
migrate.sh             Wrapper over the CLI
docker-compose.yml     ClickHouse and PostgreSQL for the integration tests
```
