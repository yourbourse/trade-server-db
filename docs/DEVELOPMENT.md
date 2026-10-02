<div align="center">

# Development

**Building, testing and extending the migrator**

[Setup](#setup) · [Build](#build) · [Tests](#tests) · [Layout](#project-layout) · [Common tasks](#common-tasks) · [Conventions](#conventions) · [CI](#ci)

</div>

---

## Setup

| Requirement | Notes |
|-------------|-------|
| .NET SDK 10.0.301+ | Pinned in `global.json` with `rollForward: latestPatch` |
| Docker | For the integration tests: `docker compose up -d` |
| An IDE with EditorConfig support | Rider and Visual Studio both read `.editorconfig`; several rules are Rider-specific |

```bash
git clone https://github.com/yourbourse/trade-server-db.git
```

```bash
dotnet build TradeServerDb.slnx
```

> **Note:** The solution is `TradeServerDb.slnx`, the XML-based format - the only one committed and the one CI
> builds. If your tooling generates a `TradeServerDb.sln` beside it, keep it out of commits.

---

## Build

```bash
dotnet build TradeServerDb.slnx
```

```bash
dotnet build TradeServerDb.slnx -c Release --nologo
```

`Directory.Build.props` applies to every project: `net10.0`, implicit usings, nullable enabled, C# 14, and
**`TreatWarningsAsErrors`** — a warning fails the build, so fix it rather than working around it.

Running the CLI without the wrapper:

```bash
dotnet run --project src/Migrator/Migrator.csproj -- --provider postgres --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=..." --dry-run
```

`migrate.sh` is the usual entry point: it builds in Release and runs from the repository root so the SQL folders
are read from the working copy. Skip the rebuild with `TSDB_NO_BUILD=1`.

> **Note:** `Migrator.csproj` links `clickhouse/**/*.sql` and `psql/**/*.sql` into the build output and the
> publish output. A published migrator carries its scripts; a repository checkout still wins, because the runner
> prefers the folders next to the working directory.

---

## Tests

Two suites in one project, separated by the `Category` trait.

### Unit tests

No database required. 60 tests covering the pure logic: file deduplication, unresolved-variable detection,
statement splitting, identifier validation, literal escaping, option and user-definition parsing, access-level
validation, secret masking and hashing.

```bash
dotnet test src/Migrator.Tests/Migrator.Tests.csproj --filter "Category!=Integration"
```

| Test file | Covers |
|-----------|--------|
| `Execution/DbUpRunnerTests.cs` | `DeduplicateFiles` ordering and override semantics, `FindUnresolvedVariables` |
| `Execution/ScriptSplittingTests.cs` | `----` splitting and comment-only chunks, both providers |
| `Extensions/SqlSafetyTests.cs` | Identifier validation, dialect-specific literal escaping |
| `Handlers/MigrationHandlerTests.cs` | Provider parsing, default script folder, `--users` / `--variables` parsing, masking, hashing |

### Integration tests

Run the real migration against the containers from `docker-compose.yml`.

```bash
docker compose up -d
```

```bash
ASPNETCORE_ENVIRONMENT=integration dotnet test src/Migrator.Tests/Migrator.Tests.csproj --filter "Category=Integration"
```

Each test provisions its own database, so assertions about what a first and a second run apply hold regardless of
what a previous run left behind; the database is dropped again on the way out. What they assert:

- The schema is created, and a second run applies nothing — on both engines.
- `--force` re-applies the re-runnable scripts.
- `--users` creates the users, and a password containing quotes, backslashes and comment markers still
  authenticates.
- A user name that is not a plain identifier is refused.
- Table names passed as variables are honoured.
- A dry run reports pending scripts without touching the database.
- Re-provisioning an exporter account with `Access=ro` leaves it read-only — the write grants are revoked.
- Rotating a user whose name is a prefix of another user's (`reader` and `reader2`) rewrites only its own journal
  entry.

Connection strings come from [`appsettings.integration.json`](../src/Migrator.Tests/appsettings.integration.json),
overridable with `ClickHouse__ConnectionString` and `Postgres__ConnectionString`.

> **Warning:** `ASPNETCORE_ENVIRONMENT=integration` selects that settings file. Without it no settings file is
> loaded at all and the tests find no server.

---

## Project layout

```
trade-server-db/
├── clickhouse/                         ClickHouse scripts
│   ├── 20260922_01_create_ticks.sql        incremental (folder root)
│   ├── post/run_on_every_build.sql         re-runnable
│   ├── users/exporter.sql                  re-runnable, one pass per --users entry
│   └── users/ro.sql
├── psql/                               PostgreSQL scripts
│   ├── 20260101_01_create_trades.sql       incremental (folder root)
│   ├── 20260101_02_create_export_cursor.sql
│   ├── 20260101_03_create_daily_statements.sql
│   ├── 20260101_04_create_order_snapshots.sql
│   ├── 20260101_05_create_position_snapshots.sql
│   ├── post/ users/ views/                 re-runnable, same phases as clickhouse/
├── src/
│   ├── Migrator/                       The CLI
│   │   ├── Program.cs                      options, Serilog, host, DI, exit code
│   │   ├── CommandLineOptions.cs           every flag
│   │   ├── Config/DatabaseUser.cs          a parsed --users entry
│   │   ├── Execution/
│   │   │   ├── DbUpRunner.cs               phase walk, dedup, preprocessing
│   │   │   ├── ExecutionOrder.cs           the five-phase table
│   │   │   ├── DbMigratorRequest.cs        everything a run needs
│   │   │   └── DatabaseProvider.cs
│   │   ├── Extensions/
│   │   │   ├── SqlSafety.cs                identifier validation, literal escaping
│   │   │   └── StringExtensions.cs         hashing, secret masking
│   │   ├── Handlers/MigrationHandler.cs    target resolution, variables, reporting
│   │   └── Logging/                        DbUp → Serilog bridge
│   ├── DbUp.PSQL/                      DbUp provider for PostgreSQL
│   ├── DbUp.ClickHouse/                DbUp provider for ClickHouse
│   └── Migrator.Tests/                 Unit + integration tests
├── .github/workflows/ci.yaml           build, unit tests, integration tests, published results
├── docker-compose.yml                  ClickHouse + PostgreSQL for testing
├── migrate.sh                          build-and-run wrapper
└── docs/                               this documentation
```

---

## Common tasks

### Add a migration

Covered in full by [Writing Migrations](WRITING_MIGRATIONS.md). The short version: incremental scripts go in the
folder root named `YYYYMMDD_NN_what_it_does.sql`, re-runnable ones in `views/` / `users/` / `post/`, use
the table variables rather than literal names, mirror the change in the other provider, and verify that a second
run applies nothing.

### Add a command line option

1. Add the property to `CommandLineOptions` with its `[Option]` attribute and help text.
2. Print it in the argument dump in `Program.cs` — mask it with `MaskSecrets` if it can carry a secret.
3. Thread it through `MigrationHandler` into `DbMigratorRequest`, which is the contract `DbUpRunner` consumes.
4. Document it in [Configuration](CONFIGURATION.md) and, if it changes what operators do, the
   [Operations Guide](OPERATIONS_GUIDE.md).

### Add a template variable

Derived values are built in `MigrationHandler.BuildVariables`. If the new variable names a database object, add
it to the `identifierVariables` array so it is validated after `--variables` has been applied. Then document it
in [Template Variables](TEMPLATE_VARIABLES.md) — a variable that is not documented is a variable the next person
will pass with `--variables` instead.

### Add a provider

The work is one project plus three touch points:

1. A new `src/DbUp.<Engine>/` project: connection manager (with statement splitting), journal (matching the
   `schema_versions` shape), script executor, object parser, and a fluent extension.
2. `DatabaseProvider` — a new enum value.
3. `MigrationHandler` — `ParseProvider` spellings, `GetDefaultScriptFolder`, and the database-name reader.
4. `DbUpRunner` — `EnsureDatabaseExists` and `CreateUpgradeEngineBuilder`, plus whether the engine supports
   `WithTransactionPerScript`.
5. `SqlSafety.EscapeLiteral` — how that dialect escapes a quote.

Then a script folder with the schema translated, and integration tests mirroring the existing ones.

### Debug a script that fails on the server

- `--dry-run` shows what *would* run, in order.
- The failure log prints the script name, the server error and the full **processed** script contents — that is
  the text the server received, after substitution.
- On ClickHouse, `Script block number` is the index of the statement within the script after splitting.

---

## Conventions

Enforced by `.editorconfig` (Rider-flavoured, honoured by other IDEs where they can):

| Rule | Value |
|------|-------|
| Indentation | 4 spaces |
| Max line length | 200 |
| Braces | Next line; required on `if`/`else` even for a single statement |
| `var` | For built-in types |
| Private modifier | Explicit |
| Blank line around members | 1 |
| Warnings | Errors, everywhere |

Beyond formatting, the house style in this repository:

- **Comments explain *why*, not *what*.** The existing code and SQL comment the surprising decisions — why
  `mutations_sync = 2`, why the engines switch on `--cluster`, why an identifier is validated instead of bound.
  Match that density: a mechanical restatement of the next line is noise.
- **Test names read as sentences.** `[Fact(DisplayName = "ParseUsers Should reject an incomplete user
  definition")]` with a matching method name, and `// arrange` / `// act` / `// assert` sections.
- **Provider parity.** A change to one `DbUp.*` project usually has a counterpart in the other; if it does not,
  say why in a comment.
- **Nothing internal in the repository.** No YB hostnames, environment names or real credentials — this
  repository is written to be readable by the clients running these databases.

---

## CI

`.github/workflows/ci.yaml` runs on every pull request and on pushes to `master`:

| Step | Command |
|------|---------|
| Build | `dotnet build TradeServerDb.slnx -c Release` |
| Unit tests | `dotnet test … --filter "Category!=Integration"` |
| Integration tests | `dotnet test … --filter "Category=Integration"` with `ASPNETCORE_ENVIRONMENT=integration` |
| Results | Both JUnit reports parsed into the job summary and a **Test results** check run, with an annotation on each failing test |
| Reports | JUnit XML uploaded as the `test-reports` artefact, always |

Service containers: `clickhouse/clickhouse-server:26.7` on `18223` and `postgres:18` on `15432` — the same ports
as `docker-compose.yml`, so the integration tests need no separate configuration locally or in CI.

The publishing step is `mikepenz/action-junit-report`, pinned to the commit behind its `v6.5.0` tag rather than
to a moving major tag. It only reports: `dotnet test` is what fails the job on a failing test. It is skipped
when the build itself failed, because `require_tests` would then fail on an empty glob and bury the real error.

---

## See also

[Architecture](ARCHITECTURE.md) · [ClickHouse Provider](CLICKHOUSE_PROVIDER.md) ·
[Writing Migrations](WRITING_MIGRATIONS.md) · [Contributing](CONTRIBUTING.md)
