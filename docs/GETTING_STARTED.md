<div align="center">

# Getting Started

**From an empty database to a migrated schema, step by step**

[Prerequisites](#prerequisites) · [Install](#step-1-get-the-repository) · [Local databases](#step-2-start-a-database-to-practise-on) · [First migration](#step-4-apply-the-schema) · [Verify](#step-5-verify) · [Next steps](#next-steps)

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

Every command below is run from the repository root, so `cd trade-server-db` first. Nothing else to install:
`migrate.sh` restores and builds on first run.

### Running without bash

`migrate.sh` is a convenience wrapper. On a machine with no bash — Windows PowerShell or `cmd` — build once and
call the binary directly; every option is identical:

```powershell
dotnet build TradeServerDb.slnx -c Release
```

```powershell
dotnet src\Migrator\bin\Release\net10.0\Migrator.dll --provider postgres --connection-string "Host=...;Port=5432;Database=trading;Username=postgres;Password=..." --dry-run
```

Run it from the repository root, as the wrapper does, so `psql/` and `clickhouse/` are picked up from the working
copy. On Windows, Git Bash and WSL both run `migrate.sh` unchanged.

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
./migrate.sh --provider postgres --connection-string "Host=localhost;Port=15432;Database=trade_demo;Username=postgres;Password=passw0rd" --dry-run
```

The output ends with a per-phase list of pending scripts:

```
Dry run: 2 script(s) would be applied for (root)
 - [psql] 20260101_01_create_trades.sql
 - [psql] 20260101_02_create_export_cursor.sql
```

> **Note:** On a database that does not exist yet the journal cannot be read, so the migrator logs a warning and
> reports **every** script as pending. That is expected on a first dry run.

---

## Step 4: Apply the schema

Drop `--dry-run` and add the two users the export pipeline needs:

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

> **Important:** On ClickHouse the migrator does **not** create `trades` and `export_cursor` — a ClickHouse
> deployment provisions those itself, and this repository only creates the journal, the users and their grants.
> The grants succeed whether or not the tables exist yet. See
> [Schema Reference](SCHEMA_REFERENCE.md#overview) for the definitions the migrator expects to find.

---

## Step 5: Verify

Three things are worth checking after a first run.

**The tables exist.**

```sql
-- PostgreSQL: expects trades, export_cursor, schema_versions
SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';
```

```sql
-- ClickHouse: expects schema_versions, plus whatever the deployment provisioned
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
[psql/users] exporter.sql for trade_exporter::7f3c…
[psql/post] run_on_every_build.sql::a1b2…
```

**A second run does nothing.** This is the property that makes the migrator safe to call from a deployment
pipeline:

```bash
./migrate.sh --provider postgres --connection-string "..."
```

```
Executed scripts: 0
```

---

## Step 6: Point it at a real database

Nothing changes except the connection string. Two habits worth keeping:

- Pass the connection string through the environment so it does not land in your shell history:
  `export TSDB_CONNECTION_STRING="Host=…"`, then `./migrate.sh --provider postgres`.
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
