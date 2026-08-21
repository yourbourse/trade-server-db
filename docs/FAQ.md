<div align="center">

# Frequently Asked Questions

**Short answers to the questions that come up most**

[General](#general) · [Running it](#running-it) · [Schema](#schema) · [Scripts](#scripts) · [Security](#security) · [Development](#development)

</div>

---

## General

### What is this repository?

The schema for the databases the **trade server** exports trading data into, plus the tool that applies it. The
trade server writes trades and an export cursor to an external PostgreSQL or ClickHouse database; this repository
owns the other half — creating and evolving the tables, indexes and users that the export needs.

### Why plain SQL rather than a code-first migration tool?

The schema is written to by the trade server, not by an application that owns a model of it, and it has to exist
identically on two engines with very different dialects. Plain SQL applied by
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

Whichever the trade server's export pipeline is configured to write to. PostgreSQL gives you transactional
writes and exact deduplication; ClickHouse gives you analytical query performance over large volumes. The schema
is the same either way.

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

### Can I rename the `trades` and `export_cursor` tables?

Yes — they are configurable in the trade server's export configuration, and the scripts template both names.
Pass them on every run:

```bash
--variables trades_table=trades_export cursor_table=trades_export_cursor
```

Nothing records the names on the database side, so a run without them creates a second, empty set of tables under
the default names.

### Why does the ClickHouse `trades` table have no `id` column?

ClickHouse does not generate row ids, and the sorting key `(sender_id, trade_id)` already identifies a row. The
guide's identity column has no equivalent, so it is left out rather than faked.

### How are duplicate trades handled?

Export delivery is at-least-once, so the same trade can arrive twice after a restart:

| | PostgreSQL | ClickHouse |
|---|------------|------------|
| Mechanism | `UNIQUE (sender_id, trade_id)` | `ReplacingMergeTree` sorted by `(sender_id, trade_id)` |
| When | immediately — the second insert is rejected | when parts merge |
| Reading | always exact | add `FINAL` when duplicates must not be visible |

### Why do I get `DECIMAL_OVERFLOW` on ClickHouse?

`Decimal` cannot represent `NaN`, and non-trading records (Balance, Credit, Fee) may carry `NaN` in fields that do
not apply to them. Either have the exporter write `0`, or switch those columns to `Float64` and accept the loss of
exactness for monetary values.

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

ClickHouse **24.x or newer** (the driver sends settings older servers reject). PostgreSQL is tested against 17,
and the scripts use nothing newer than `GENERATED ALWAYS AS IDENTITY`, so 13+ is fine.

### Why is the SQL at the repository root instead of under `src/`?

So it can be browsed and reviewed without digging through the C#. The build links it into the output, so a
published migrator still carries its scripts.

---

## See also

[Getting Started](GETTING_STARTED.md) · [Troubleshooting](TROUBLESHOOTING.md) ·
[Operations Guide](OPERATIONS_GUIDE.md) · [Schema Reference](SCHEMA_REFERENCE.md)
