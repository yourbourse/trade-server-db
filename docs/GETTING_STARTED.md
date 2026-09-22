<div align="center">

# Getting Started

**From an empty database to a migrated schema, step by step**

[Prerequisites](#prerequisites) · [.NET SDK](#install-the-net-sdk) · [Install](#step-1-get-the-repository) · [Windows](#running-on-windows) · [Local databases](#step-2-start-a-database-to-practise-on) · [First migration](#step-4-apply-the-schema) · [Verify](#step-5-verify) · [Next steps](#next-steps)

</div>

---

## Prerequisites

| Requirement | Version | How to check |
|-------------|---------|--------------|
| **.NET SDK** | 10.0.301+ (pinned in `global.json`) | `dotnet --version` |
| **Git** | any | `git --version` |
| **Bash** | any — Git Bash and WSL both work on Windows | `bash --version` |
| **PostgreSQL** | 14+ | `psql --version` |
| **ClickHouse** | **24.x or newer** | `SELECT version()` |
| **Docker** | optional, for the local test databases | `docker --version` |

> **Note:** `ClickHouse.Driver` 1.3.0 sends settings that older servers reject, so ClickHouse 24.x is a hard
> floor. PostgreSQL has no such constraint — the scripts use nothing newer than `GENERATED ALWAYS AS IDENTITY`.

### Install the .NET SDK

`migrate.sh` **compiles the migrator before it runs it**, so the SDK is not optional and the runtime alone is not
enough. Install it before the first run:

| Platform | Command |
|----------|---------|
| Windows | `winget install Microsoft.DotNet.SDK.10` |
| macOS | `brew install --cask dotnet-sdk` |
| Ubuntu / Debian | `sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0` |
| Anything else | The [.NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0), or the [dotnet-install script](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script) |

On Windows the wrapper runs under Git Bash or WSL. Git Bash calls the Windows SDK installed above; **WSL is a
separate machine with its own SDK** — installing on one side does not install on the other.

Check what landed:

```bash
dotnet --list-sdks
```

`global.json` pins **10.0.301** with `rollForward: latestPatch`, which accepts `10.0.301` or any later patch in
the same `10.0.3xx` feature band — and nothing else. `latestPatch` does **not** roll forward across bands, so a
`10.0.4xx` SDK on its own will not satisfy it either. Several SDKs can sit side by side; `global.json` picks the
one it wants. An empty list, or one showing only `9.x`, fails the build with
`The specified SDK version could not be found` — see
[Troubleshooting](TROUBLESHOOTING.md#dotnet-build-cannot-find-the-sdk).

The first build also **restores NuGet packages from nuget.org**, so it needs network access once. After that the
packages are cached under `~/.nuget/packages` and later builds work offline.

You will also need, on the database you are migrating:

- **A database that already exists, or permission to create one.** The migrator issues a
  create-database-if-missing step before the first phase, so an account with `CREATEDB` on PostgreSQL, or the
  equivalent ClickHouse grant, can start from nothing.
- **DDL rights** — it creates tables, indexes, roles and users.

---

## Step 1: Get the repository

```bash
git clone https://github.com/yourbourse/trade-server-db.git
```

Every command below is run from the repository root, so `cd trade-server-db` first.

### Build it once

`migrate.sh` restores and builds on every run, but doing it once by hand separates *toolchain* problems from
*database* problems — a failure here is the SDK or the package feed, never a connection string:

```bash
dotnet build TradeServerDb.slnx -c Release
```

Release is what the wrapper builds, so the run is not wasted: this is the binary `migrate.sh` will execute.
Expect `Build succeeded`, and a `Migrator.dll` in `src/Migrator/bin/Release/net10.0/`.

> **Note:** `TreatWarningsAsErrors` is on for every project, so a warning fails the build. A clean checkout has
> none — if one appears, it came from a local edit.

Once it builds, `TSDB_NO_BUILD=1` in front of any `migrate.sh` command skips the rebuild and runs the binary
left by the previous build. Useful when a pipeline stage has already built; leave it off while editing SQL.

### Running on Windows

`migrate.sh` is a bash script, and Windows has no bash of its own. There are three ways to run it, and the one
you pick decides which .NET SDK the build uses:

| Shell | `migrate.sh` | Which SDK it builds with |
|-------|--------------|--------------------------|
| **Git Bash** (ships with Git for Windows) | Runs unchanged — **the recommended option** | The Windows SDK |
| **WSL** | Runs unchanged | A **Linux** SDK installed inside WSL — *not* the Windows one |
| **PowerShell / `cmd`** | Cannot run it — call the binary directly | The Windows SDK |

#### Git Bash

Nothing to configure. Open Git Bash in the repository root and use every command in this guide as written:

```bash
./migrate.sh --provider postgres --connection-string "Host=localhost;Port=15432;Database=trade_demo;Username=postgres;Password=passw0rd" --users "Access=exporter;User=trade_exporter;Password=passw0rd" --dry-run
```

Git Bash rewrites POSIX-looking arguments into Windows paths before the program sees them, so
`--scripts-root /d/work/trade-server-db` arrives as `D:/work/trade-server-db`. That is the form the migrator
wants, so it works in your favour — but it is why a path argument echoed back in the log looks different from
what you typed.

#### WSL

The wrapper runs unchanged, with one trap that costs people an afternoon: **WSL is a separate machine with its
own SDK.** Installing the .NET SDK on Windows does *not* install it in WSL, and inside WSL the build fails with
`dotnet: command not found` even when `dotnet --list-sdks` on the Windows side lists a dozen of them.

Windows executables *do* leak into WSL through PATH interop, but only under their real filename — so
`which dotnet` finds nothing while `which dotnet.exe` returns `/mnt/c/Program Files/dotnet/dotnet.exe`. Do not
reach for that: `migrate.sh` invokes `dotnet`, and building a Linux-side checkout with the Windows SDK is not a
combination worth debugging. Install the Linux SDK instead:

```bash
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
```

The repository is reachable from WSL under `/mnt/d/...`, but builds across that boundary are markedly slower than
in the WSL filesystem. For occasional migrations it does not matter; if you work in WSL daily, clone into
`~/` instead.

#### PowerShell and `cmd`

No wrapper. Build once and call the binary directly — every option is identical:

```powershell
dotnet build TradeServerDb.slnx -c Release
```

```powershell
dotnet src\Migrator\bin\Release\net10.0\Migrator.dll --provider postgres --connection-string "Host=localhost;Port=15432;Database=trade_demo;Username=postgres;Password=passw0rd" --users "Access=exporter;User=trade_exporter;Password=passw0rd" --dry-run
```

Run it **from the repository root**, as the wrapper does, so `psql/` and `clickhouse/` are picked up from the
working copy.

> **Warning:** **Always quote the connection string.** A connection string is full of `;`, which PowerShell reads
> as a statement separator — unquoted, `--connection-string Host=localhost;Port=5432;Database=trading` reaches
> the migrator as `Host=localhost` and PowerShell tries to run `Port=5432` as a command. Anything after the first
> `;` is lost, **`--dry-run` included** — so the guard you thought you had is silently gone. The same applies to
> each `--users` value.

Two more differences from the bash examples elsewhere in these docs:

| Bash | PowerShell |
|------|------------|
| `\` at end of line to continue | Backtick `` ` `` — or just keep it on one line |
| `export TSDB_CONNECTION_STRING="…"` | `$env:TSDB_CONNECTION_STRING = "…"` |

Multi-value flags behave the same in both: several quoted values follow **one** flag.

```powershell
$env:TSDB_CONNECTION_STRING = "Host=localhost;Port=15432;Database=trade_demo;Username=postgres;Password=passw0rd"
```

```powershell
dotnet src\Migrator\bin\Release\net10.0\Migrator.dll --provider postgres --users "Access=exporter;User=trade_exporter;Password=passw0rd" "Access=ro;User=trade_reader;Password=passw0rd" --dry-run
```

---

## Step 2: Start a database to practise on

Do the first run against something disposable. The repository ships a compose file with both engines on
non-default ports, so it will not collide with anything already installed:

```bash
docker compose up -d
```

| Engine | Host port | Credentials |
|--------|-----------|-------------|
| ClickHouse | `18223` (HTTP), `19223` (native) | `default` / `passw0rd` |
| PostgreSQL | `15432` | `postgres` / `passw0rd` |

> **Note:** These credentials belong to the throwaway containers. They sit in `docker-compose.yml` in plain text
> on purpose — never reuse them anywhere that matters.

---

## Step 3: Look before you leap

`--dry-run` reports the scripts that would be applied and touches nothing — not even the create-database step:

```bash
./migrate.sh --provider postgres --connection-string "Host=localhost;Port=15432;Database=trade_demo;Username=postgres;Password=passw0rd" --users "Access=exporter;User=trade_exporter;Password=passw0rd" "Access=ro;User=trade_reader;Password=passw0rd" --dry-run
```

`--users` is required on every run, dry runs included — the accounts are re-granted each time so they pick up
whatever tables the migration adds, and a run without the flag would silently grant nothing. Step 4 explains the
two accounts; for now, pass them exactly as above.

The output ends with a per-phase list of pending scripts:

```
Dry run: 5 script(s) would be applied for (root)
 - [psql] 20260101_01_create_trades.sql
 - [psql] 20260101_02_create_export_cursor.sql
 - [psql] 20260101_03_create_daily_statements.sql
 - [psql] 20260101_04_create_order_snapshots.sql
 - [psql] 20260101_05_create_position_snapshots.sql
```

> **Note:** On a database that does not exist yet the journal cannot be read, so the migrator logs a warning and
> reports **every** script as pending. That is expected on a first dry run.

---

## Step 4: Apply the schema

Drop `--dry-run`. The two accounts stay — they are the ones the export pipeline needs, and every later migration
has to name them again so their grants keep up with the schema:

```bash
./migrate.sh --provider postgres --connection-string "Host=localhost;Port=15432;Database=trade_demo;Username=postgres;Password=passw0rd" --users "Access=exporter;User=trade_exporter;Password=passw0rd" "Access=ro;User=trade_reader;Password=passw0rd"
```

> **Warning:** `--users` and `--variables` take a **list**, so several values follow **one** flag, separated by
> spaces. Repeating the flag fails with `Option 'u, users' is defined multiple times`.

The run walks the five phases in order and finishes with `Migration completed`. The exit code is `0`.

Now the same against ClickHouse:

```bash
./migrate.sh --provider clickhouse --connection-string "Host=localhost;Port=18223;Database=trade_demo;Username=default;Password=passw0rd" --users "Access=exporter;User=trade_exporter;Password=passw0rd"
```

> **Important:** The ClickHouse run creates a different schema, not a copy of the PostgreSQL one: ticks are the
> one entity the trade server exports to ClickHouse, so the run creates the `ticks` table, the journal, the
> users and their grants, and none of the five PostgreSQL tables. See
> [Schema Reference](SCHEMA_REFERENCE.md#overview).

---

## Step 5: Verify

Three things are worth checking after a first run.

**The tables exist.**

```sql
-- PostgreSQL: expects trades, export_cursor, the three daily statement tables, schema_versions
SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';
```

```sql
-- ClickHouse: expects ticks and schema_versions
SHOW TABLES FROM trade_demo;
```

**The journal recorded the run.**

```sql
SELECT script_name, applied_at FROM schema_versions ORDER BY applied_at;
```

Incremental scripts appear under their file name; re-runnable ones carry an `::md5` suffix:

```
[psql] 20260101_01_create_trades.sql
[psql] 20260101_02_create_export_cursor.sql
[psql] 20260101_03_create_daily_statements.sql
[psql] 20260101_04_create_order_snapshots.sql
[psql] 20260101_05_create_position_snapshots.sql
[psql/users] exporter.sql for trade_exporter::7f3c…
[psql/post] run_on_every_build.sql::a1b2…
```

**A second run does nothing.** This is the property that makes the migrator safe to call from a deployment
pipeline:

```bash
./migrate.sh --provider postgres --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=passw0rd" "Access=ro;User=trade_reader;Password=passw0rd"
```

```
Executed scripts: 0
```

The accounts are passed again and still nothing is applied: the `users/` scripts are journaled by content, so
re-supplying an unchanged account is a no-op. They are re-applied only once the scripts change — which is
exactly when a new table needs granting.

---

## Step 6: Point it at a real database

Nothing changes except the connection string. Two habits worth keeping:

- Pass the connection string through the environment so it does not land in your shell history:
  `export TSDB_CONNECTION_STRING="Host=…"`, then `./migrate.sh --provider postgres --users "…"`.
- Keep the account passwords in a secret store the migration command reads from. `--users` is required on every
  run, so they are needed every time and should never be typed by hand.
- Run `--dry-run` first. Every time.

See the [Operations Guide](OPERATIONS_GUIDE.md) for the full routine, including ClickHouse clusters.

---

## Next Steps

| If you want to… | Read |
|-----------------|------|
| Understand every option | [Configuration](CONFIGURATION.md) |
| Run this against production | [Operations Guide](OPERATIONS_GUIDE.md) |
| Add a table or change a column | [Writing Migrations](WRITING_MIGRATIONS.md) |
| Know what the columns mean | [Schema Reference](SCHEMA_REFERENCE.md) |
| Understand how it works inside | [Architecture](ARCHITECTURE.md) |
| Fix an error you just hit | [Troubleshooting](TROUBLESHOOTING.md) |
