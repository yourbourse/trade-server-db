<div align="center">

# Frequently Asked Questions

**Short answers to the questions that come up most**

[General](#general) · [Running it](#running-it) · [Schema](#schema) · [Scripts](#scripts) · [Security](#security) · [Development](#development)

</div>

---

## General

### What is this repository?

The schema for the databases the **trade server** exports trading data into, plus the tool that applies it. The
trade server writes trades, daily statements and an export cursor to PostgreSQL and ticks to ClickHouse; this
repository owns the other half — creating and evolving the tables, indexes and users that the export needs.

### Why plain SQL rather than a code-first migration tool?

The schema is written to by the trade server, not by an application that owns a model of it, and it spans two
engines with very different dialects. Plain SQL applied by
[DbUp](https://dbup.readthedocs.io/) keeps the actual DDL reviewable — what you read in
`psql/20260101_01_create_trades.sql` is exactly what runs, on the engine it was written for.

### Can I roll back a migration?

**No.** The migrator only rolls forward, by design. A mistake is corrected by adding a new script, never by
undoing an old one. That is a deliberate trade: a rollback path that is rarely exercised is a rollback path that
does not work when you need it.

### Is it safe to run twice?

Yes — that is the central property. Incremental scripts are journalled by file name and never run again;
re-runnable scripts re-run only when their content changes. A second run reports `Executed scripts: 0`.

### Which engine should I use?

Both, if you export both kinds of entity. The trade server sends trades, daily statements, their snapshots and
the export cursor to PostgreSQL and ticks to ClickHouse, and rejects a pipeline that pairs an entity with the
other engine. Run the migrator once per engine you export to; each run creates that engine's tables.

---

## Running it

### Do I need both engines?

No. Run the migrator once per engine you actually use, each with its own `--provider` and connection string.

### What permissions does the migrator need?

DDL rights on the target database, plus the right to create it if it does not exist. On ClickHouse, the `users/`
phase additionally needs access-management privileges. Use a dedicated migration account — **not** the exporter
account, which deliberately has no DDL rights.

### Where do I put the connection string?

`--connection-string`, or `$TSDB_CONNECTION_STRING` when you would rather keep it out of your shell history and
the process list. The flag wins if both are present.

### Can I run `migrate.sh` on Windows?

Yes — from **Git Bash**, which comes with Git for Windows and runs it unchanged with no setup. WSL runs it too,
but WSL is a separate machine and needs its own **Linux** .NET SDK; the SDK you installed on Windows is invisible
to it. PowerShell and `cmd` have no bash, so there the wrapper is out and you call
`dotnet src\Migrator\bin\Release\net10.0\Migrator.dll` directly with the identical options — quoting the
connection string, because PowerShell splits arguments on `;`. See
[Running on Windows](GETTING_STARTED.md#running-on-windows).

### Do I need to install the .NET SDK, or is the runtime enough?

The SDK. `migrate.sh` compiles `src/Migrator` before every run, and compiling needs an SDK. The runtime alone is
enough only for a `dotnet publish` output shipped to the target host — see
[Deploying a self-contained copy](OPERATIONS_GUIDE.md#deploying-a-self-contained-copy).

### Can I run it from a deployment pipeline?

Yes; it is built for that. It never prompts, exits non-zero on the first failure, and applies nothing when
nothing changed. See [Operations Guide](OPERATIONS_GUIDE.md#running-from-a-pipeline).

### Do I need the repository checked out to run it?

No. `dotnet publish` copies `psql/` and `clickhouse/` next to the binary, so a published migrator is
self-contained. A repository checkout takes precedence when present, which is what makes editing a `.sql` file
and re-running work without a rebuild.

### What does `--force` actually do?

Re-applies every **re-runnable** script — `pre/`, `views/`, `users/`, `post/` — even when unchanged. Incremental
scripts are untouched. Reach for it when a re-runnable script's effect was lost without its content changing,
such as a view dropped by hand.

### Why did a dry run say every script is pending?

Because the database does not exist yet, so the journal cannot be read — and a dry run is not allowed to create
anything. The migrator logs a warning and reports everything as pending rather than failing.

---

## Schema

### Which tables does the migrator own?

Nine on PostgreSQL that the trade server writes to — `trades`, `daily_statements_export`,
`daily_statements_order_snapshots`, `daily_statements_position_snapshots`, `trading_account_events`,
`client_events`, `symbol_events`, `group_events` and `export_cursor` — one on ClickHouse — `ticks` — plus the four
tables the migrator creates and a trigger maintains from an event log, `trading_accounts`, `clients`, `symbols`
and `groups`, and its own `schema_versions` journal on each engine. Each exported table matches one export
pipeline in the trade server's `export_config.json`, and the PostgreSQL pipelines share the one cursor table.
Column by column: [Schema Reference](SCHEMA_REFERENCE.md).

### Can I rename the exported tables?

Yes — every one of them is configurable in the trade server's export configuration, and the scripts template all
ten names. Pass the ones you renamed on every run:

```bash
--variables trades_table=trades_export cursor_table=trades_export_cursor daily_statements_table=statements_export
```

```bash
--variables ticks_table=market_ticks
```

Nothing records the names on the database side, so a run without them grants on — and on PostgreSQL creates — a
second set of tables under the default names.

### What is the relationship between a statement and its snapshots?

They share `time_statement`, the scheduled end-of-day timestamp. One statement run writes one row per account to
`daily_statements_export`, one row per **working** order to `daily_statements_order_snapshots` and one row per
open position to `daily_statements_position_snapshots`, all stamped with the same value. There are no foreign
keys — join on `(sender_id, time_statement, trading_account_id)`.

### Does the migrator create the ClickHouse tables?

**Yes** — the one there is. ClickHouse receives ticks and nothing else, so `clickhouse/20260922_01_create_ticks.sql`
creates the `ticks` table and the `users/` phase grants on it. The PostgreSQL tables have no ClickHouse
counterpart, because the trade server never writes those entities there.

### Why is there no cursor table on ClickHouse?

A ClickHouse pipeline keeps its export position on disk rather than in the database, so there is nothing for a
cursor table to hold. After a crash it starts again from position 0 — the *Export Setup* guide describes the
consequences.

### How are duplicate records handled?

Export delivery is at-least-once, so the same record can arrive twice after a restart. Each table has its own
deduplication key — `(sender_id, trade_id)` on trades, `(sender_id, time_statement, trading_account_id)` on
statements, the same pair plus `order_id` or `position_id` on the snapshots, and
`(sender_id, short_symbol_name, created_time, sequence)` on ticks:

| | PostgreSQL | ClickHouse |
|---|------------|------------|
| Mechanism | a `UNIQUE` index on the key | `ReplacingMergeTree` sorted by the key |
| When | immediately — the second insert is rejected | when parts merge |
| Reading | always exact | add `FINAL` when duplicates must not be visible |

### Can I point the migrator at a database that was set up by hand?

Yes. Every create statement uses `IF NOT EXISTS`, so it adopts what is already there. Note the corollary: it will
**not** correct a table whose columns differ from the script, so compare the schema by hand when adopting a
database you did not create.

### Is the schema a stable interface?

Treat it as one. The clients running these databases build reporting on top of it, so renaming or dropping a
column is a breaking change that belongs in the release notes even if nothing inside YourBourse reads it.

---

## Scripts

### What is the difference between the folder root and `views/` / `post/`?

The root is **incremental** — applied once, journalled by file name, never again. The sub-folders are
**re-runnable** — journalled with the MD5 of their processed content, so they re-run when their content changes.
Full detail: [Writing Migrations](WRITING_MIGRATIONS.md).

### Why do ClickHouse scripts need `----break----`?

The ClickHouse HTTP interface accepts exactly one statement per request, so each statement has to be its own
chunk. PostgreSQL does not need separators — a whole file is sent as one command inside one transaction.

### Can I use `##variables##` of my own?

Yes: `--variables key=value` becomes `##key##`. They are applied last, so they also override derived values. They
are substituted verbatim without validation, so treat the flag as trusted operator input.

### What happens if I mistype a variable?

The run fails **before** anything reaches the database, naming the placeholder and listing the known variables.
That is deliberate — an unresolved `##` would otherwise come back as a server syntax error pointing at a
character offset.

### Can I add deployment-specific SQL without forking?

Yes. Pass several folders and a later folder's same-named file overrides an earlier one:

```bash
--scripts psql custom-deployment
```

### Can I edit a script that was already applied?

No. An incremental script will not run again, so the file and the deployed database drift apart silently. Add a
new script.

---

## Security

### Is the SQL vulnerable to injection?

Everything the migrator sends as a **value** is a bound parameter. Identifiers cannot be bound on either engine,
so those are validated against `^[A-Za-z_][A-Za-z0-9_]{0,62}$` instead, and the one free-form value that must sit
inside a literal — a user password — is escaped for the target dialect. Details and threat model:
[Security](SECURITY.md).

### Will a password with special characters break?

No. A password containing `'`, `\`, `;` or `--` is stored intact and still authenticates; there is an integration
test that logs in with one on both engines.

### Are passwords written to the logs?

No. They are masked in the console output, in `logs/migrator.log`, and in error messages quoting a `--users`
entry. Your own terminal scrollback and shell history are not the migrator's to clean — prefer the environment
variable and a secret store.

### Does the repository contain any real credentials?

No. What looks like credentials is either a throwaway container password (`docker-compose.yml`, the CI service
containers) or a documentation placeholder such as `db.example.com`.

---

## Development

### How do I run the tests?

```bash
dotnet test src/Migrator.Tests/Migrator.Tests.csproj --filter "Category!=Integration"
```

```bash
docker compose up -d
```

```bash
ASPNETCORE_ENVIRONMENT=integration dotnet test src/Migrator.Tests/Migrator.Tests.csproj --filter "Category=Integration"
```

### Why is there a custom DbUp provider for ClickHouse?

Because DbUp does not ship one, and ClickHouse needs several accommodations: one statement per request, `{name:Type}`
parameters, `ALTER TABLE … DELETE` instead of `DELETE`, multiple result sets from one query, and `ON CLUSTER` on
every DDL statement. See [ClickHouse Provider](CLICKHOUSE_PROVIDER.md).

### Can I add another database engine?

Yes — one new `DbUp.<Engine>` project plus a handful of switch statements. The steps are listed in
[Development](DEVELOPMENT.md#add-a-provider).

### Which ClickHouse and PostgreSQL versions are supported?

ClickHouse **24.x or newer** (the driver sends settings older servers reject). PostgreSQL **14+**. CI and the
compose file run the newest stable releases of both — ClickHouse 26.7 and PostgreSQL 18.

### Why is the SQL at the repository root instead of under `src/`?

So it can be browsed and reviewed without digging through the C#. The build links it into the output, so a
published migrator still carries its scripts.

---

## See also

[Getting Started](GETTING_STARTED.md) · [Troubleshooting](TROUBLESHOOTING.md) ·
[Operations Guide](OPERATIONS_GUIDE.md) · [Schema Reference](SCHEMA_REFERENCE.md)
