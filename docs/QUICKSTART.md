<div align="center">

# Quick Start

**For people who have used a migration tool before**

[Two commands](#two-commands) · [What just happened](#what-just-happened) · [Flags worth knowing](#flags-worth-knowing) · [Gotchas](#gotchas)

</div>

---

## Two commands

First: **.NET SDK 10.0.301+** (`dotnet --list-sdks`), because the wrapper compiles before it migrates. Not
installed? [Install the .NET SDK](GETTING_STARTED.md#install-the-net-sdk).

On Windows, run these from **Git Bash** — it runs the wrapper unchanged. WSL works too but needs its own Linux
SDK; PowerShell cannot run the wrapper at all. [Running on Windows](GETTING_STARTED.md#running-on-windows).

```bash
./migrate.sh --provider postgres --connection-string "Host=db;Port=5432;Database=trading;Username=postgres;Password=..." --users "Access=exporter;User=trade_exporter;Password=..." "Access=ro;User=trade_reader;Password=..." --dry-run
```

```bash
./migrate.sh --provider postgres --connection-string "Host=db;Port=5432;Database=trading;Username=postgres;Password=..." --users "Access=exporter;User=trade_exporter;Password=..." "Access=ro;User=trade_reader;Password=..."
```

Swap `postgres` for `clickhouse` and the port for `8123` to migrate the other engine. `migrate.sh` builds
`src/Migrator` in Release and runs it from the repository root, so `psql/` and `clickhouse/` are read from disk —
editing a `.sql` file and re-running needs no rebuild.

Prefer the environment for secrets:

```bash
export TSDB_CONNECTION_STRING="Host=db;Port=5432;Database=trading;Username=postgres;Password=..."
```

---

## What just happened

Plain SQL files applied by [DbUp](https://dbup.readthedocs.io/), in five phases, journalled in
`schema_versions`:

| Order | Folder | Re-runnable | What goes there |
|-------|--------|-------------|-----------------|
| 1 | `pre/` | yes | Anything that must exist before the tables. Optional |
| 2 | *folder root* | **no** | Incremental migrations, applied once in file-name order |
| 3 | `views/` | yes | Views and materialized views |
| 4 | `users/` | yes | Roles and users, once per matching `--users` entry |
| 5 | `post/` | yes | Anything that must run after the schema |

Incremental scripts are journalled by file name and never run twice. Re-runnable scripts are journalled with the
MD5 of their **processed** content, so they re-run exactly when their content changes. There is **no rollback** —
a mistake is corrected with a new script.

---

## Flags worth knowing

| Flag | Why you would reach for it |
|------|----------------------------|
| `-n, --dry-run` | Report pending scripts, touch nothing |
| `-f, --force` | Re-apply every re-runnable script regardless of hash |
| `-c, --cluster <name>` | ClickHouse: `ON CLUSTER` everywhere plus the `Replicated*` engines |
| `-u, --users` | `Access=exporter;User=…;Password=…` — one flag, several values |
| `-v, --variables` | `key=value` becomes `##key##` in scripts. Applied last, overrides derived values |
| `-s, --scripts` | Extra script folders; a later folder overrides a same-named file from an earlier one |
| `-j, --journal-table` | Rename the journal, default `schema_versions` |

Full list: [Configuration](CONFIGURATION.md), or `./migrate.sh --help`.

---

## Gotchas

- **`--users`, `--variables` and `--scripts` are lists.** One flag, values separated by spaces. Repeating the
  flag is a parse error.
- **`--provider` is required.** There is no default and no auto-detection from the connection string.
- **An unknown `##placeholder##` fails the run** before anything reaches the database, naming the placeholder.
- **Identifiers are validated** against `^[A-Za-z_][A-Za-z0-9_]{0,62}$` — journal table, database name, table
  names, cluster name, and every `--users` name. See [Security](SECURITY.md).
- **ClickHouse needs `----break----` between statements** — one statement per HTTP request. PostgreSQL does not,
  and a file with no separator runs as a single command inside one transaction.
- **ClickHouse 24.x is the minimum server version.**

---

## Where next

[Getting Started](GETTING_STARTED.md) for the guided version · [Operations Guide](OPERATIONS_GUIDE.md) for
production runs · [Writing Migrations](WRITING_MIGRATIONS.md) before you add a script ·
[Troubleshooting](TROUBLESHOOTING.md) when it goes wrong.
