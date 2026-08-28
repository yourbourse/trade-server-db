# trade-server-db

Schema migrations for the databases the **trade server** exports trading data into.

The trade server writes trades, daily statements, their order and position snapshots, and an export cursor to an
external database — see the *Export Setup* guide for how the export pipelines are configured. This repository
owns the other half: creating and evolving the tables, indexes, views and users that export needs.

Two providers are supported:

| Provider | Scripts | Driver | Journal |
|---|---|---|---|
| PostgreSQL | [`psql/`](psql) | Npgsql, via DbUp | `schema_versions` |
| ClickHouse | [`clickhouse/`](clickhouse) | `ClickHouse.Driver` (official, HTTP) | `schema_versions` |

Migrations are plain SQL files applied by [DbUp](https://dbup.readthedocs.io/) — no ORM and no generated DDL, so
what you read in a script is what runs.

> **There is no rollback.** The migrator only rolls forward. A mistake is corrected by adding a new script, not
> by undoing an old one.

## Quick start

`migrate.sh` compiles the migrator before running it, so the **.NET SDK 10.0.301 or newer** has to be installed
first — the runtime alone is not enough. `winget install Microsoft.DotNet.SDK.10` on Windows,
`brew install --cask dotnet-sdk` on macOS, `sudo apt-get install -y dotnet-sdk-10.0` on Ubuntu, or the
[.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0) anywhere else. Confirm it:

```bash
dotnet --list-sdks
```

Then, from the repository root, build once — this is the same Release build the wrapper runs, so it also proves
the toolchain before a connection string is involved:

```bash
dotnet build TradeServerDb.slnx -c Release
```

Now migrate:

```bash
# PostgreSQL
./migrate.sh --provider postgres \
             --connection-string "Host=db.example.com;Port=5432;Database=trading;Username=postgres;Password=..." \
             --users "Access=exporter;User=trade_exporter;Password=..." \
                     "Access=ro;User=trade_reader;Password=..."

# ClickHouse
./migrate.sh --provider clickhouse \
             --connection-string "Host=ch.example.com;Port=8123;Database=trading;Username=default;Password=..." \
             --users "Access=exporter;User=trade_exporter;Password=..." \
                     "Access=ro;User=trade_reader;Password=..."
```

`--users` provisions the accounts the export pipeline needs — `exporter` for the trade server, `ro` for
reporting. It takes a list, so several values follow one flag. Omit it and the schema is still created, but no
account is granted access to it; see [Users & Permissions](docs/USERS_AND_PERMISSIONS.md).

`migrate.sh` builds `src/Migrator` and runs it from the root of the repository, so the scripts in `psql/` and
`clickhouse/` are picked up as they are on disk. Every argument is passed straight through, so
`./migrate.sh --help` lists the full set of options.

The build happens on **every** run — cheap once the first one has restored packages from nuget.org, and it means
an edited `.sql` file never needs a manual rebuild. `TSDB_NO_BUILD=1 ./migrate.sh …` skips it and runs the binary
from the previous build, for pipelines that build in an earlier stage. Full prerequisites and per-platform SDK
instructions: [Getting Started](docs/GETTING_STARTED.md#install-the-net-sdk).

### On Windows

`migrate.sh` is a bash script. **Git Bash runs it unchanged and needs no setup** — that is the path of least
resistance, and every command in these docs works as written. WSL also runs it unchanged, but WSL is a separate
machine and needs its own **Linux** SDK; the Windows one does not count. PowerShell and `cmd` cannot run the
wrapper at all — build once and call the binary directly:

```powershell
dotnet src\Migrator\bin\Release\net10.0\Migrator.dll --provider postgres --connection-string "Host=...;Port=5432;Database=trading;Username=postgres;Password=..." --dry-run
```

Quote the connection string. PowerShell splits on `;`, so unquoted it truncates at the first one and silently
drops every later flag — `--dry-run` included. Details and the rest of the Windows differences:
[Running on Windows](docs/GETTING_STARTED.md#running-on-windows).

## Documentation

The rest of this file is a summary. The full documentation lives in [`docs/`](docs/README.md).

### Setup & configuration

| Guide | Description |
|---|---|
| [Getting Started](docs/GETTING_STARTED.md) | Prerequisites, first migration against a throwaway database, and how to tell it worked |
| [Quick Start](docs/QUICKSTART.md) | Minimal path for people who already know what a migrator does |
| [Configuration](docs/CONFIGURATION.md) | Every option, the environment variables, logging, and exit codes |

### Usage

| Guide | Description |
|---|---|
| [Operations Guide](docs/OPERATIONS_GUIDE.md) | Live databases, the upgrade routine, ClickHouse clusters, pipelines, recovery |
| [Writing Migrations](docs/WRITING_MIGRATIONS.md) | Phases, incremental vs re-runnable, separators, and the rules that keep a script re-runnable |
| [Users & Permissions](docs/USERS_AND_PERMISSIONS.md) | The `exporter` and `ro` roles, their grants on both engines, and password rotation |

### Reference

| Reference | Description |
|---|---|
| [Schema Reference](docs/SCHEMA_REFERENCE.md) | The trade, daily statement, snapshot, cursor and journal tables and the two views, column by column, with the type mapping |
| [Template Variables](docs/TEMPLATE_VARIABLES.md) | Every `##variable##`, where its value comes from, and how it is validated |
| [Security](docs/SECURITY.md) | What is bound, what is validated, how passwords are escaped, and the threat model |

### Architecture & development

| Document | Description |
|---|---|
| [Architecture](docs/ARCHITECTURE.md) | Components, the execution pipeline, journalling, and where the providers differ |
| [ClickHouse Provider](docs/CLICKHOUSE_PROVIDER.md) | The custom DbUp provider and the ClickHouse behaviour behind it |
| [Development](docs/DEVELOPMENT.md) | Building, the test suites, project layout, and how to extend the migrator |
| [Contributing](docs/CONTRIBUTING.md) | Branch and commit conventions, the review checklist, and what a good migration PR contains |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Errors you will actually hit, and what clears them |
| [FAQ](docs/FAQ.md) | Short answers to the questions that come up most |

## Command line

| Option | Description |
|---|---|
| `-p, --provider` | `clickhouse` or `postgres`. Required |
| `-l, --connection-string` | The database to migrate. Falls back to `$TSDB_CONNECTION_STRING` |
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
| `##trades_table##` | `trades`, unless `--variables trades_table=...` says otherwise |
| `##daily_statements_table##` | `daily_statements_export`, unless `--variables` says otherwise |
| `##order_snapshots_table##` | `daily_statements_order_snapshots`, unless `--variables` says otherwise |
| `##position_snapshots_table##` | `daily_statements_position_snapshots`, unless `--variables` says otherwise |
| `##cursor_table##` | `export_cursor`, unless `--variables cursor_table=...` says otherwise |
| `##assembly_version##` | The version of the migrator that applied the script |
| `##user_name##`, `##user_password##` | The `users/` phase only, one pass per `--users` entry |
| `##cluster_name##`, `##cluster_name_suffix##` | ClickHouse only. Empty unless `--cluster` is given |
| `##merge_tree_engine##`, `##replacing_merge_tree_engine##` | ClickHouse only. `MergeTree`/`ReplacingMergeTree`, or the `Replicated*` variants when `--cluster` is given |

Anything else can be passed with `--variables key=value`.

A script containing a `##name##` that is not in this list and was not passed with `--variables` is **rejected
before it reaches the database**, naming the placeholder — otherwise the `##` travels through to the server and
comes back as a syntax error pointing at a character offset. If you want a literal table name, just write it; the
five table variables are templated only because the trade server's export configuration makes those names
configurable — a deployment that renamed one passes the name with `--variables`.

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

* `--journal-table`, `##database_name##`, `##cluster_name##`, every table variable and every
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
2. Use the table variables — `##trades_table##`, `##daily_statements_table##`, `##order_snapshots_table##`,
   `##position_snapshots_table##`, `##cursor_table##` — rather than hard-coded table names. They are all
   configurable in the trade server's export configuration.
3. Check what it would do: `./migrate.sh --provider postgres --connection-string "..." --dry-run`.
4. Apply it, then run it a second time and confirm nothing is applied.
5. Mirror the change in the other provider's folder if it applies to both.

Never edit a script that has already been applied somewhere: an incremental script will not run again, and the
deployed database will silently differ from the file. Add a new one.

## Schema

Both engines carry the same five tables the trade server writes to, matching the *Export Setup* guide — but only
PostgreSQL has them created here:

| Table | Default name | PostgreSQL | ClickHouse |
|---|---|---|---|
| Trades | `trades` | created by the migrator | **provisioned by the deployment**, not by this repository |
| Daily statements | `daily_statements_export` | created by the migrator | provisioned by the deployment |
| Order snapshots | `daily_statements_order_snapshots` | created by the migrator | provisioned by the deployment |
| Position snapshots | `daily_statements_position_snapshots` | created by the migrator | provisioned by the deployment |
| Export cursor | `export_cursor` | created by the migrator | provisioned by the deployment |
| Journal, users and grants | `schema_versions` | created by the migrator | created by the migrator |

ClickHouse grants reference the tables by name, which succeeds whether or not they exist yet. The
definitions the migrator expects — and the copies the ClickHouse integration tests migrate against, under
`src/Migrator.Tests/TestScripts/clickhouse/` — are documented in
[Schema Reference](docs/SCHEMA_REFERENCE.md).

On top of `trades` there are two views:

| View | Default name | Rows | PostgreSQL | ClickHouse |
|---|---|---|---|---|
| Trades history | `trades_history` | the executed and cancelled trades — `trade_type` 0, 1, 14, 15 | created by the migrator | provisioned by the deployment |
| Cash transfers | `cash_transfers` | every other `trade_type`: balances, credits, fees, commissions, interest, dividends and the rest | created by the migrator | provisioned by the deployment |

The two filters are complements, so every row of `trades` is visible through exactly one of them.

They follow the table they read. A `GRANT` tolerates an object that does not exist yet, but a `CREATE VIEW`
resolves the table it selects from — so shipping the ClickHouse views would turn a missing `trades` table from a
failing export into a failing migration. Their definitions live beside the ClickHouse table definitions in
`src/Migrator.Tests/TestScripts/clickhouse/views/`; `clickhouse/users/ro.sql` grants on them by name either way.

A daily statement is exported as three rows in three tables — the statement itself, one snapshot per **working**
order and one per open position — all sharing the scheduled end-of-day `time_statement`, which is what ties them
together.

Every exported table is append-only. Delivery is at-least-once, so the same record can arrive twice after a
restart:

* **PostgreSQL** deduplicates with a unique index: `(sender_id, trade_id)` on trades, and
  `(sender_id, time_statement, …)` on the statement and snapshot tables. The second insert is rejected.
* **ClickHouse** has no unique index, so each table is a `ReplacingMergeTree` whose sorting key is that same key
  — it collapses duplicates when the parts merge, so query with `FINAL` when they must not be visible before
  that. The primary key stays at `(sender_id)`, since that is the sparse index held in memory.

The ClickHouse columns follow the guide's types: `BIGINT` → `Int64`, `SMALLINT` → `Int16`, `NUMERIC` →
`Decimal(38, 18)`, `VARCHAR` → `String`, `TIMESTAMP WITH TIME ZONE` → `DateTime64(6, 'UTC')`. The guide's
identity column has no equivalent and is left out — ClickHouse does not generate row ids, and the sorting key
already identifies a row.

> `Decimal` cannot hold `NaN`, and the guide states that non-trading records (Balance, Credit, Fee and the
> other accounting types) carry `0` or `NaN` in the fields that do not apply to them. An exporter that sends
> `NaN` gets `DECIMAL_OVERFLOW` and has to write `0` instead. Switch the `Decimal(38, 18)` columns to `Float64`
> if the writer cannot be changed. The exception is the snapshot columns the guide marks nullable —
> `avg_fill_price`, `stop_price`, `limit_price`, `stop_loss`, `take_profit` — where a `NaN` means *not set* and
> the exporter writes `NULL`.

`export_cursor` records how far the export has got, one row per `(sender_id, table_name)`, so all five pipelines
share it. On PostgreSQL the cursor row and the batch of records are written in the same transaction, which is
what makes the cursor authoritative. ClickHouse has no `UPDATE`, so the exporter appends a new position and
`ReplacingMergeTree` collapses the history, using `last_written_position` as the version column so the highest
position always wins.

The `users/` scripts provision the two roles the guide describes: `exporter` (INSERT and SELECT on the four
exported tables, INSERT, SELECT and UPDATE on the cursor) and `ro` (SELECT on all five).

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
psql/                  PostgreSQL scripts: root = incremental, views/ users/ post/ = re-runnable
clickhouse/            ClickHouse scripts: users/ post/ only - no tables and no views, see Schema
src/DbUp.ClickHouse/   DbUp provider for ClickHouse (connection manager, journal, executor)
src/DbUp.PSQL/         DbUp provider for PostgreSQL
src/Migrator/          The CLI
src/Migrator.Tests/    Unit and integration tests
  TestScripts/clickhouse/  The ClickHouse tables and views this repository does not ship
docs/                  Full documentation, indexed in docs/README.md
migrate.sh             Wrapper over the CLI
docker-compose.yml     ClickHouse and PostgreSQL for the integration tests
```
