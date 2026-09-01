<div align="center">

# Architecture

**How a command line turns into applied SQL**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![DbUp](https://img.shields.io/badge/DbUp-6.0-2C3E50)](https://dbup.readthedocs.io/)
[![Npgsql](https://img.shields.io/badge/Npgsql-9.0-336791?logo=postgresql&logoColor=white)](https://www.npgsql.org/)
[![ClickHouse.Driver](https://img.shields.io/badge/ClickHouse.Driver-1.3-FFCC01?logo=clickhouse&logoColor=black)](https://clickhouse.com/)

[System overview](#system-overview) · [Pipeline](#the-execution-pipeline) · [Phases](#phase-resolution) · [Preprocessing](#preprocessing) · [Journalling](#journalling) · [Providers](#provider-differences) · [Projects](#projects)

</div>

---

## System overview

```
┌──────────────────────────────────────────────────────────────────────┐
│                             migrate.sh                               │
│              dotnet build  →  dotnet Migrator.dll "$@"               │
└───────────────────────────────┬──────────────────────────────────────┘
                                │
┌───────────────────────────────▼──────────────────────────────────────┐
│                          Migrator (CLI)                              │
│                                                                      │
│  Program.cs            parse options, configure Serilog, host, DI    │
│         │                                                            │
│         ▼                                                            │
│  MigrationHandler      provider + connection string → one target     │
│         │              build template variables, validate identifiers│
│         ▼                                                            │
│  DbUpRunner            walk phases, collect files, preprocess,       │
│         │              hand each phase to DbUp                       │
└─────────┼────────────────────────────────────────────────────────────┘
          │
          │  UpgradeEngineBuilder per phase
          ▼
┌──────────────────────────┐         ┌──────────────────────────────────┐
│      DbUp.PSQL           │         │       DbUp.ClickHouse            │
│  connection manager      │         │  connection manager (splits on   │
│  journal (schema_versions)│        │    ----break----)                │
│  script executor         │         │  journal (schema_versions,       │
│  object parser           │         │    ON CLUSTER aware)             │
│                          │         │  script executor, object parser  │
└────────────┬─────────────┘         └───────────────┬──────────────────┘
             │ Npgsql                                │ ClickHouse.Driver (HTTP)
             ▼                                       ▼
      ┌─────────────┐                         ┌─────────────┐
      │ PostgreSQL  │                         │ ClickHouse  │
      └─────────────┘                         └─────────────┘
             ▲                                       ▲
             └───────────────┬───────────────────────┘
                             │
                    psql/ and clickhouse/
                  plain .sql files on disk
```

Three ideas carry the whole design:

| Idea | Consequence |
|------|-------------|
| **The SQL is the source of truth** | Scripts live at the repository root, reviewable without reading C#. The tool applies them; it does not generate them |
| **The journal decides what runs** | Applying twice is a no-op. That is what makes it safe in a pipeline |
| **Provider differences stay in the providers** | The phase walk, deduplication, preprocessing and journal naming are provider-independent |

---

## The execution pipeline

```
CommandLineOptions
        │
        ▼
MigrationHandler.Handle
        │  ParseProvider(--provider)                     → ClickHouse | Postgres
        │  ResolveConnectionString                       → --connection-string, else $TSDB_CONNECTION_STRING
        │  GetDatabaseName(connection string)            → ##database_name##
        │  ValidateIdentifier(--journal-table)
        │  ParseUsers(--users)                           → [ (access, user, password) ]
        │  BuildVariables                                → derived, then --variables, then validation
        ▼
DbMigratorRequest ─────────────────────────────────────► DbUpRunner.Run
                                                              │
        ┌─────────────────────────────────────────────────────┘
        │  for each of the 5 phases:
        │      collect *.sql from every --scripts folder
        │      DeduplicateFiles  (last folder wins)
        │
        │  EnsureDatabaseExists   (skipped on --dry-run)
        │
        │  for each phase, in order:
        │      substitute ##variables##, hash the result
        │      reject leftover ##placeholders##
        │      build a DbUp UpgradeEngine for the phase
        │      PerformUpgrade  (or GetScriptsToExecute on --dry-run)
        │      stop the run if the phase failed
        ▼
List<(phase, DatabaseUpgradeResult)>  →  logged per phase, exception on failure
```

Each phase gets its **own** DbUp upgrade engine. That is what lets one phase be journalled normally while
another is journalled to a `NullJournal` under `--force`, and it is why a failure stops the run at a phase
boundary.

---

## Phase resolution

The five phases are a static table in `DbUpRunner`:

| OrderId | SubFolder | ReRunnable | Content resolver |
|---------|-----------|------------|------------------|
| 1 | `pre` | yes | default |
| 2 | *(none — folder root)* | **no** | default |
| 3 | `views` | yes | default |
| 4 | `users` | yes | users (one script per matching `--users` entry) |
| 5 | `post` | yes | default |

For every phase, each `--scripts` folder is resolved against `--scripts-root`, falling back to the directory
holding `Migrator.dll` — the build copies `psql/` and `clickhouse/` next to the binary so a published migrator is
self-contained, while a repository checkout takes precedence.

A `--scripts` folder that exists in neither location fails the run. A missing **sub**-folder is skipped, which is
why `pre/` and `views/` can be absent.

### Folder overrides

Files are collected from every folder, then deduplicated on *last folder segment + file name*, keeping the entry
from the **last** folder. That is the deployment-override mechanism:

```
--scripts psql custom

psql/post/ttl.sql      ─┐
custom/post/ttl.sql    ─┴─► custom/post/ttl.sql wins
psql/post/other.sql       ─► applied
psql/views/x.sql       ─┐
psql/post/x.sql        ─┴─► different phases, both applied
```

Surviving files are ordered by folder order, then by path. The folder order also becomes DbUp's
`RunGroupOrder`, so scripts from an earlier folder run before scripts from a later one within a phase.

---

## Preprocessing

Before a file becomes a DbUp `SqlScript`:

1. **Substitute** every `##name##` with its value (plain text replacement).
2. **Hash** the result — MD5, used for the journal entry of a re-runnable script.
3. **Reject** any leftover `##[A-Za-z0-9_]+##`, naming each distinct placeholder once and listing the known
   variables.

Step 3 is the guard that keeps an unresolved placeholder from reaching the server, where it would come back as a
syntax error pointing at a character offset.

The `users/` phase resolver differs: for each `--users` entry whose `Access` matches the file name, it adds
`##user_name##` (validated as an identifier) and `##user_password##` (escaped for the dialect), then substitutes
and hashes per user — so one file yields one script per user, each with its own journal entry.

> **Note:** DbUp has its own variable-substitution and preprocessor mechanism. This repository does the
> substitution itself instead, because the hash of the *processed* content is what drives re-runnable
> journalling — the file has to be fully resolved before it is hashed. The provider preprocessors are
> deliberately pass-through.

---

## Journalling

One table, `schema_versions` by default, with the same shape on both engines:

| Column | Purpose |
|--------|---------|
| `script_name` | The journal entry — see the naming table below |
| `applied_at` | UTC timestamp, microsecond precision |

| Kind | Entry | Behaviour |
|------|-------|-----------|
| Incremental | `[psql] 20260101_01_create_trades.sql` | Present → never runs again |
| Re-runnable | `[psql/post] run_on_every_build.sql::<md5>` | Runs when the hash differs |
| Users | `[psql/users] exporter.sql for trade_exporter::<md5>` | One entry per user |

The re-runnable mechanism lives in the journal implementations: on `StoreExecutedScript`, if the script name
contains `::`, every existing entry whose name starts with the part before `::` is deleted first. Exactly one row
per re-runnable script survives, so the journal cannot grow without bound and a content change is enough to
trigger a re-run.

`--force` bypasses this by pointing the re-runnable phases at DbUp's `NullJournal`: nothing is read, nothing is
written, everything runs.

Both journals also override the table-existence check so the schema and table names travel as bound parameters
rather than being pasted into the query.

---

## Provider differences

| Concern | PostgreSQL | ClickHouse |
|---------|------------|------------|
| Driver | Npgsql 9 | `ClickHouse.Driver` 1.3 (official, HTTP) |
| Statement splitting | On `----` if present, otherwise the whole file as one command | On `----`, always — one statement per request |
| Transactions | `WithTransactionPerScript()` — a failing script rolls back | None available |
| Journal engine | plain table, `PRIMARY KEY (script_name)` | `MergeTree` / `ReplicatedMergeTree`, ordered by `(script_name, applied_at)` |
| Journal delete | `DELETE … WHERE starts_with(script_name, @prefix)` | `ALTER TABLE … DELETE WHERE startsWith(…) SETTINGS mutations_sync = 2` |
| Create database | `EnsureDatabase.For.PostgresDatabase` | `create database if not exists` + a read-only/availability wait |
| Cluster support | n/a | `ON CLUSTER`, `Replicated*` engines, replicated journal |
| Parameter syntax | `@name` | `{name:Type}` |
| Schema | `public` | none — the database *is* the namespace |

The ClickHouse side needed enough custom work to deserve its own document:
[ClickHouse Provider](CLICKHOUSE_PROVIDER.md).

Everything else — the phase walk, deduplication, preprocessing, journal naming, `--force`, `--dry-run` — is
shared, which is why the two providers behave the same way from the operator's point of view.

---

## Dry run

`--dry-run` changes three things:

- The create-database step is skipped.
- Each phase calls `GetScriptsToExecute()` instead of `PerformUpgrade()`.
- If the journal cannot be read — normal on a database that does not exist yet — the migrator logs a warning and
  reports every script as pending rather than failing.

The result is reported through the same per-phase result objects as a real run, so the output format is
identical.

---

## Projects

| Project | Contents |
|---------|----------|
| `src/Migrator/` | `Program.cs` (options, Serilog, host), `MigrationHandler` (target + variables), `DbUpRunner` (phases), `SqlSafety`, extensions |
| `src/DbUp.PSQL/` | Connection manager, `TableJournal` base + `PostgresTableJournal`, script executor, object parser, pass-through preprocessor |
| `src/DbUp.ClickHouse/` | The same set for ClickHouse, plus the `EnsureDatabase` extension and the multi-result-set journal read |
| `src/Migrator.Tests/` | Unit tests for the pure logic, integration tests against real containers |

Runtime: .NET 10, `TreatWarningsAsErrors`, nullable enabled, Serilog to console and rolling JSON file, DI through
`Microsoft.Extensions.Hosting`, options parsed by `CommandLineParser`.

Both `DbUp.*` projects deliberately mirror each other's structure so a change to one has an obvious counterpart
in the other.

---

## See also

[ClickHouse Provider](CLICKHOUSE_PROVIDER.md) · [Development](DEVELOPMENT.md) ·
[Writing Migrations](WRITING_MIGRATIONS.md) · [Security](SECURITY.md)
