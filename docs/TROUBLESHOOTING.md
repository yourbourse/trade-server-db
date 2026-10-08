<div align="center">

# Troubleshooting

**The errors you will actually hit, and what clears them**

[Running the wrapper](#running-the-wrapper) · [Startup](#startup-and-options) · [Connection](#connection-problems) · [Scripts](#script-and-template-problems) · [PostgreSQL](#postgresql-specific) · [ClickHouse](#clickhouse-specific) · [Journal](#journal-and-re-run-behaviour) · [Tests](#build-and-test-problems) · [Diagnosing](#diagnosing-anything-else)

</div>

---

## Running the wrapper

### `./migrate.sh: Permission denied`

The execute bit was lost — most often by copying the repository through a filesystem that does not carry it, or
by unpacking an archive rather than cloning.

```bash
chmod +x migrate.sh
```

Or bypass it entirely: `bash migrate.sh --provider postgres …`.

### `migrate.sh: line 2: $'\r': command not found`

The file was checked out with Windows line endings and bash is reading the carriage returns as part of each
command.

```bash
git config core.autocrlf input
```

```bash
git rm --cached -r . && git reset --hard
```

The repository ships a `.gitattributes` that pins `*.sh` to LF, so a fresh clone is unaffected — this happens to
clones made before that, or when a file is edited by a tool that rewrites line endings.

### `migrate.sh` will not run at all — no bash on this machine

Build once and call the binary directly, with the same options:

```powershell
dotnet build TradeServerDb.slnx -c Release
```

```powershell
dotnet src\Migrator\bin\Release\net10.0\Migrator.dll --provider postgres --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=..." --dry-run
```

Run it from the repository root so the SQL folders are found. On Windows this is only necessary in PowerShell or
`cmd` — **Git Bash and WSL both run the wrapper unchanged**, and Git Bash needs no setup at all. See
[Running on Windows](GETTING_STARTED.md#running-on-windows).

### PowerShell: `Port=5432: The term 'Port=5432' is not recognized…`

The connection string was not quoted. PowerShell treats `;` as a statement separator, so it splits the argument
and tries to run each fragment as a command:

```powershell
# wrong - everything after the first ';' is lost
dotnet …\Migrator.dll --provider postgres --connection-string Host=localhost;Port=5432;Database=trading
```

```powershell
# right
dotnet …\Migrator.dll --provider postgres --connection-string "Host=localhost;Port=5432;Database=trading;Username=postgres;Password=..."
```

> **Warning:** The migrator still starts, with a truncated connection string — and **every flag after the first
> `;` is dropped too, `--dry-run` among them.** An unquoted command you believed was a dry run is not one. Quote
> every `--connection-string`, every `--users` value and every `--variables` value.

### PowerShell: the trailing `\` in a documented command does nothing

The multi-line examples in these docs are bash, where `\` continues a line. PowerShell uses a backtick
`` ` ``. Simplest fix is to put the command on one line.

### `Migrator binary not found: …/bin/Release/net10.0/Migrator.dll`

`TSDB_NO_BUILD=1` was set before anything had been built, or the build configuration differs. Run once without
`TSDB_NO_BUILD`, or set `TSDB_CONFIGURATION=Debug` to match an existing Debug build.

---

## Startup and options

### `Required option 'p, provider' is missing`

`--provider` has no default and is not inferred from the connection string.

```bash
./migrate.sh --provider postgres --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=..."
```

### `Option 'u, users' is defined multiple times`

`--users`, `--variables` and `--scripts` take **lists**. Several values follow **one** flag:

```bash
--users "Access=exporter;User=trade_exporter;Password=..." "Access=ro;User=trade_reader;Password=..."
```

Not `--users "..." --users "..."`.

### `Unknown provider 'mysql'. Supported values: 'clickhouse', 'postgres'`

Accepted spellings: `clickhouse`, `ch`, `postgres`, `postgresql`, `psql`, `pg`. Case and surrounding whitespace
are ignored.

### `Unable to parse the user definition '…'. Expected format: Access=…;User=…;Password=…`

All three keys are required. The password is masked in the error. A password may contain `=` and `;` - the entry
is split only at the semicolons that introduce `Access=`, `User=` or `Password=`, and only the first `=` of a
pair separates key from value.

---

## Connection problems

### `A connection string is required. Supply --connection-string or set TSDB_CONNECTION_STRING`

Neither source produced a value. Note that an **empty** environment variable counts as absent.

```bash
export TSDB_CONNECTION_STRING="Host=db;Port=5432;Database=trading;Username=migrator;Password=..."
```

### `The connection string does not specify a database name`

The migrator reads the database out of the connection string — for `##database_name##`, for the journal, and for
the create-database step. Add `Database=…`.

### The run hangs, then fails with a socket or timeout error

**Causes & solutions:**

| Cause | Check |
|-------|-------|
| Wrong port | PostgreSQL `5432`, ClickHouse **HTTP** `8123` — not the native `9000` port |
| Containers not running | `docker compose ps`, then `docker compose up -d` |
| Local containers use non-default ports | `15432` for PostgreSQL, `18223` for ClickHouse |
| Firewall or wrong host | Connect with `psql` / `clickhouse-client` from the same machine to confirm |

### `permission denied` / `Not enough privileges` during the first phase

The migration account needs DDL rights, and the right to create the database if it does not exist yet. Do not run
migrations as the exporter account — it deliberately has neither.

---

## Script and template problems

### `<file> uses ##something##, which the migrator does not know about`

A placeholder was not resolved. The message lists the known variables.

**Solutions:**

1. Typo in the placeholder — compare against [Template Variables](TEMPLATE_VARIABLES.md).
2. A ClickHouse-only variable (`##cluster_name_suffix##`, `##merge_tree_engine##`, …) used in a PostgreSQL
   script. Those exist only for the ClickHouse provider.
3. A genuinely custom value — pass it: `--variables something=value`.

> **Note:** You cannot have a literal `##word##` in a script; the check has no escape syntax. Pass those
> characters through a variable if you really need them.

### `Script folder doesn't exist: <name>`

A folder passed to `--scripts` was found neither under `--scripts-root` (default: the working directory) nor next
to `Migrator.dll`. Run from the repository root, or pass `--scripts-root`.

Missing *sub*-folders (`pre/`, `views/`) are skipped silently — only top-level script folders are required.

### `… is not a valid SQL identifier`

The value goes into DDL as text and must match `^[A-Za-z_][A-Za-z0-9_]{0,62}$`. A hyphen is the usual culprit:
`trade-exporter` → `trade_exporter`. Applies to `--journal-table`, the database name, the table names, the
cluster name and every `--users` name. Background: [Security](SECURITY.md).

### A script did not run

**Solutions:**

| Cause | Check |
|-------|-------|
| Already applied | `SELECT script_name FROM schema_versions ORDER BY applied_at` — an incremental script runs once, ever |
| Re-runnable and unchanged | Its hash matches; use `--force` to re-apply anyway |
| Overridden by a later `--scripts` folder | A same-named file in a later folder wins |
| Wrong phase folder | Only `pre`, `views`, `users`, `post` and the folder root are walked |
| `users/` script with no matching entry | `Access=` must match the file name; without a matching `--users` entry the file applies nothing |

### `--force` did not restore a role

The `users/` phase only runs for the `--users` entries it is given, so `--force` on its own restores nothing —
and since the flag is required, a forced run without it does not start at all. Pass both:

```bash
./migrate.sh --provider postgres --connection-string "..." --force --users "Access=exporter;User=trade_exporter;Password=..."
```

### `--users is required, on every migration and not only the first`

The migration stopped before touching the database because no `--users` entry was given while the scripts in play
implement at least one access level.

This is deliberate. A `users/` script is instantiated once per account that selects it, so a run without
`--users` produces no user script at all and the phase reports success having applied nothing. The accounts an
earlier run created would then keep the grants of that run, and every table this migration adds would be missing
from them — surfacing later as a permission error inside the trade server, with nothing in the migration log
explaining it.

The fix is to pass the same accounts the deployment was migrated with the first time:

```bash
./migrate.sh --provider postgres --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=..." "Access=ro;User=trade_reader;Password=..."
```

Two things to know:

- **The password is re-applied on every run**, so whatever is passed becomes the account's password. Read it from
  the same secret store the connection string comes from rather than retyping it.
- **`--dry-run` needs the flag too.** A dry run exists to tell you whether the real command will work, so it
  validates the same requirement.

If you no longer know which accounts a deployment uses, the journal records them — the same `script_name` column
on both engines:

```sql
SELECT script_name FROM schema_versions WHERE script_name LIKE '%/users]%';
```

The rows read `[psql/users] exporter.sql for trade_exporter::7f3c…`, so the account name and its access level are
both there. The passwords are not — they are never stored in recoverable form — but they do not need to be:
passing a new one rotates the account to it.

### `No script implements the access level requested by: …`

The `Access` value of a `--users` entry has to match the name of a file in the users folder, so `Access=exporter`
runs `users/exporter.sql`. The message lists the access levels that do exist; the usual causes are a typo, a
capitalisation difference (the match is case-sensitive), or a `--scripts` folder set that does not carry that
role.

---

## PostgreSQL specific

### `CREATE INDEX CONCURRENTLY cannot run inside a transaction block`

A file with no separator is sent as **one command inside one implicit transaction**. Put the concurrent index in
its own chunk:

```sql
CREATE TABLE IF NOT EXISTS ...;

----break----

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_... ON ...;
```

### `role "…" already exists` or a password that did not change

The `users/` scripts create the role only when missing and then set the password unconditionally, so this should
not happen from the migrator. If it does, something outside the migrator created the role — check with
`SELECT rolname FROM pg_catalog.pg_roles`.

---

## ClickHouse specific

### `Empty query`

A chunk containing nothing but comments reached the server. The connection manager drops comment-only chunks, so
this points at a chunk that looks empty but is not — a stray `;` on its own, for instance.

### `Syntax error` on a script that looks fine

Almost always a missing `----break----`: two statements arrived in one request, and ClickHouse accepts exactly
one per request. Every statement needs its own chunk.

### Settings-related errors on connect

`ClickHouse.Driver` 1.3.0 sends settings that servers older than 24.x reject. Check `SELECT version()`.

### `Database … is in readonly-mode`

The availability check exhausted its seven attempts. On a replicated setup a replica is behind or Keeper is
unreachable — check `system.replicas` and Keeper health before re-running.

### Tables exist but only on one node

The DDL ran without `ON CLUSTER`, meaning `--cluster` was not passed. It must be passed on **every** run.
Existing tables are not converted by adding the flag later — the engine is fixed at `CREATE TABLE`.

### The migrator thinks every script is pending, on a cluster

The journal was created without `ON CLUSTER`, so each node has its own. Pass `--cluster` consistently.

### `Not enough privileges` during the `users/` phase

The account needs `ACCESS MANAGEMENT` privileges. Locally that is `CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT: 1`,
which the compose file already sets.

---

## Journal and re-run behaviour

### Every incremental script looks pending on an existing database

**Causes:**

| Cause | Check |
|-------|-------|
| Different `--journal-table` than last time | The journal is how the migrator knows what ran |
| Different database in the connection string | Journals are per database |
| Journal table dropped or truncated | Compare with what the schema actually contains |
| ClickHouse cluster, journal not `ON CLUSTER` | See above |
| Dry run on a non-existent database | Expected — the journal cannot be read, so everything is reported pending |

> **Warning:** Do not "fix" this by letting incremental scripts re-run against a schema that already has the
> objects. The scripts use `IF NOT EXISTS` throughout, so it will usually be harmless — but check before, not
> after.

### A re-runnable script runs on every migration

Its processed content is changing. The usual reason is a variable that varies between runs — `##assembly_version##`
changes with every new migrator version, which is exactly why `post/run_on_every_build.sql` re-runs and reports
the version.

### Journal rows accumulating for one script

Should not happen: each store deletes the previous entries for that script first. If you see it on ClickHouse,
the delete mutation is not completing — check `system.mutations` for failures.

---

## Build and test problems

### The build fails on a warning

`TreatWarningsAsErrors` is on for every project. Fix the cause rather than suppressing it.

### `dotnet build` cannot find the SDK

```
The specified SDK version could not be found: 10.0.301
```

`global.json` pins 10.0.301 with `rollForward: latestPatch`: `10.0.301` or a later patch in the same `10.0.3xx`
band satisfies it, nothing else does — including a `10.0.4xx` SDK, since `latestPatch` never rolls across
feature bands. See what you have:

```bash
dotnet --list-sdks
```

An empty list means only the **runtime** is installed — enough to run a published migrator, not to build one.
`migrate.sh` builds, so it needs the SDK: [Install the .NET SDK](GETTING_STARTED.md#install-the-net-sdk).

### `dotnet: command not found` when running `migrate.sh`

The SDK is not installed, or its directory is not on `PATH`. A `dotnet-install` script installation does not
touch `PATH`; add its target (`$HOME/.dotnet` by default) yourself:

```bash
export PATH="$HOME/.dotnet:$PATH"
```

**Inside WSL, this is almost always the missing Linux SDK.** WSL is a separate machine: an SDK installed on
Windows is not installed in WSL, no matter what `dotnet --list-sdks` reports from PowerShell.

```bash
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
```

PATH interop makes this confusing, because Windows executables *are* reachable from WSL under their real
filename. On a machine with no Linux SDK:

```
$ which dotnet
$ which dotnet.exe
/mnt/c/Program Files/dotnet/dotnet.exe
```

Do not use that as the fix. `migrate.sh` calls `dotnet`, not `dotnet.exe`, and pointing a Linux-side build at the
Windows SDK is not a combination worth debugging. Install the Linux SDK.

### The build fails on a NuGet restore

`NuGet.config` clears all sources and adds only nuget.org, so the first build needs to reach
`api.nuget.org` — behind a proxy or an air-gapped host it will not. Either build on a machine with access and
copy `~/.nuget/packages` over, or add an internal mirror to `NuGet.config`. Once restored, `migrate.sh` builds
offline.

### Integration tests fail with a connection error

```bash
docker compose up -d
```

```bash
ASPNETCORE_ENVIRONMENT=integration dotnet test src/Migrator.Tests/Migrator.Tests.csproj --filter "Category=Integration"
```

The tests read the compose ports from `appsettings.integration.json` on every run, from the command line or an IDE
alike. A connection error means the containers are not up yet (wait for `healthy` in `docker compose ps`), or a
`ClickHouse__ConnectionString` / `Postgres__ConnectionString` variable points somewhere else.

### `Unable to locate the repository root`

The integration tests walk up from the test binary looking for a directory containing both `clickhouse/` and
`psql/`. Run them from inside the working copy, not from a copied-out binary folder.

---

## Diagnosing anything else

**1. Read the argument dump.** The first thing a run prints is what it understood — provider, database,
variables — with passwords masked. Most surprises are visible there.

**2. Dry run.** `--dry-run` lists the pending scripts per phase without touching anything.

**3. Read the journal.**

```sql
SELECT script_name, applied_at FROM schema_versions ORDER BY applied_at;
```

**4. Read the log file.** `logs/migrator.log` is compact JSON, rolling daily, ten files kept. A failure records
the script name, the server error, and the full **processed** script — the exact text the server received.

**5. Reproduce locally.** `docker compose up -d` gives you both engines on non-default ports; run the same
command against a throwaway database.

---

## See also

[Configuration](CONFIGURATION.md) · [Operations Guide](OPERATIONS_GUIDE.md) ·
[ClickHouse Provider](CLICKHOUSE_PROVIDER.md) · [FAQ](FAQ.md)
