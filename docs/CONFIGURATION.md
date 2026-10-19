<div align="center">

# Configuration Reference

**Every option the migrator accepts, and where else it looks**

[Command line](#command-line-options) · [Target](#target-options) · [Scripts](#script-options) · [Behaviour](#behaviour-options) · [Environment](#environment-variables) · [Exit codes](#exit-codes) · [Local containers](#local-containers)

</div>

---

## Overview

The migrator is configured entirely from the command line plus one environment variable. There is no
configuration file to deploy - the integration tests read their own `appsettings.integration.json`, and nothing
else is loaded unless you add it.

```bash
./migrate.sh --provider <clickhouse|postgres> --connection-string "<connection string>" --users "<user definitions>" [options]
```

Every argument given to `migrate.sh` is passed straight through, so `./migrate.sh --help` prints the full option
list from the binary itself. Where there is no bash, call the binary directly with the same options —
`dotnet src/Migrator/bin/Release/net10.0/Migrator.dll --help`.

Options come in three groups: **what to migrate** (target), **what to apply** (scripts), and **how to behave**.

---

## Command line options

| Short | Long | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `-p` | `--provider` | **yes** | — | `clickhouse` or `postgres` |
| `-l` | `--connection-string` | no | `$TSDB_CONNECTION_STRING` | The database to migrate |
| `-s` | `--scripts` | no | folder named after the provider | Script folders to apply, in order |
| `-r` | `--scripts-root` | no | current directory, then the binary's folder | Where script folders are resolved from |
| `-c` | `--cluster` | no | none (single instance) | ClickHouse only: cluster name |
| `-u` | `--users` | no | none | Users to create, one entry per role |
| `-v` | `--variables` | no | none | Extra template variables |
| `-j` | `--journal-table` | no | `schema_versions` | Table recording applied scripts |
| `-f` | `--force` | no | `false` | Re-apply every re-runnable script |
| `-n` | `--dry-run` | no | `false` | Report what would be applied, change nothing |

---

## Target options

### `--provider`

| Value | Accepted spellings | Scripts folder |
|-------|--------------------|----------------|
| ClickHouse | `clickhouse`, `ch` | `clickhouse/` |
| PostgreSQL | `postgres`, `postgresql`, `psql`, `pg` | `psql/` |

Case and surrounding whitespace are ignored. Anything else fails immediately with the list of accepted values.
The provider decides three things: which script folder is the default, which DbUp provider runs the scripts, and
whether the ClickHouse-only variables and `--cluster` apply.

### `--connection-string`

A standard connection string for the engine — Npgsql format for PostgreSQL, `ClickHouse.Driver` format for
ClickHouse. It **must name a database**; the migrator reads the database name out of it, uses it for
`##database_name##`, and fails with *The connection string does not specify a database name* when it is absent.

Resolution order:

1. `--connection-string`
2. `$TSDB_CONNECTION_STRING`
3. Nothing — the run stops with *A connection string is required. Supply --connection-string or set TSDB_CONNECTION_STRING*

```bash
# PostgreSQL
--connection-string "Host=db.example.com;Port=5432;Database=trading;Username=postgres;Password=..."

# ClickHouse
--connection-string "Host=ch.example.com;Port=8123;Database=trading;Username=default;Password=..."
```

> **Note:** Passwords are masked wherever the migrator echoes a connection string — console, log file, and the
> argument dump at start-up. The keys treated as secret are `Password` and `Pwd`.

### `--cluster`

ClickHouse only; ignored for PostgreSQL. Giving a cluster name changes three things at once:

| | Without `--cluster` | With `--cluster my_cluster` |
|---|---|---|
| Statements | plain | `ON CLUSTER 'my_cluster'` |
| Table engines | `MergeTree` / `ReplacingMergeTree` | `ReplicatedMergeTree` / `ReplicatedReplacingMergeTree` |
| Journal engine | `MergeTree` | `ReplicatedMergeTree` |

The reason is that a single instance has no Keeper to coordinate replication, so the `Replicated*` engines are
used only when a cluster is actually requested. The cluster name is validated as an identifier.

---

## Script options

### `--scripts`

One or more folders, applied in the order given. Each is resolved against `--scripts-root`. Omit it and the
migrator uses the single folder named after the provider.

A file present in more than one folder is applied **once**, from the **last** folder that contains it. That is
the override mechanism for deployment-specific SQL:

```bash
--scripts psql custom-deployment
```

| File | Outcome |
|------|---------|
| `psql/post/ttl.sql` | overridden |
| `custom-deployment/post/ttl.sql` | **applied** |
| `psql/post/other.sql` | applied |

Files are matched on the last folder segment plus the file name, so `views/x.sql` and `post/x.sql` are different
scripts and both run.

> **Warning:** A folder listed with `--scripts` must exist, or the run fails with
> `Script folder doesn't exist: <name>`. Missing **sub**-folders (`pre/`, `views/`, …) are simply skipped.

### `--scripts-root`

The folder that `--scripts` entries are resolved against. Default: the current working directory; if a folder is
not found there, the directory holding `Migrator.dll` is tried, because the build copies `psql/` and
`clickhouse/` next to the binary so a published migrator is self-contained.

### `--journal-table`

The table that records applied scripts, default `schema_versions`. Its name is written into `CREATE TABLE`,
`INSERT` and `DELETE`, so it is validated as an identifier. Change it only if you have a genuine collision — the
journal is how the migrator knows what has already run, and pointing at a fresh journal makes every incremental
script look pending.

---

## Behaviour options

### `--users`

Semicolon-separated key/value entries. `Access` selects the script in `users/` by file name, so
`Access=exporter` runs `users/exporter.sql`:

```bash
--users "Access=exporter;User=trade_exporter;Password=passw0rd" "Access=ro;User=trade_reader;Password=passw0rd"
```

| Key | Meaning |
|-----|---------|
| `Access` | Which `users/*.sql` script to run — the file name without extension |
| `User` | The role/user name. Validated as an identifier |
| `Password` | The password. Escaped for the target dialect, never bound |

All three keys are required; anything missing fails with the expected format (password masked). An `Access`
value that matches no script **fails the run**, listing the access levels that do exist.

The password is free-form: it may contain `=` (only the first `=` of each pair splits key from value) and it may
contain `;`, because the entry is split only at the semicolons that introduce one of the three keys. The one
sequence a password cannot contain is a semicolon immediately followed by `Access=`, `User=` or `Password=`.

Full detail: [Users & Permissions](USERS_AND_PERMISSIONS.md).

### `--variables`

`key=value` pairs substituted into scripts as `##key##`:

```bash
--variables trades_table=trades_export cursor_table=trades_export_cursor ttl_days=90
```

Applied **last**, so they override the derived values. Values are substituted verbatim and are **not**
validated, because they legitimately carry things that are not identifiers — engine names, TTL numbers,
intervals. Those that name a database object (`database_name`, `cluster_name` and every table and view name)
are validated after the override, so overriding one of them with something that is not an identifier still
fails.

Full list of what is already defined: [Template Variables](TEMPLATE_VARIABLES.md).

### `--force`

Re-applies every re-runnable script — `pre/`, `views/`, `users/`, `post/` — even when its content has not
changed. Incremental scripts are unaffected: they are journalled by name and never run twice.

Use it when a re-runnable script's *effect* was lost without its *content* changing: someone dropped a view by
hand, or a role was revoked outside the migrator.

> **Warning:** `--force` re-applies the scripts it is *given inputs for*. The `users/` phase still needs its
> `--users` entries — a forced run without them re-applies `views/` and `post/` and reports `0` for `users`,
> because there is no user to write. Pass `--users` alongside `--force` when the point is to restore a role.

### `--dry-run`

Reports the pending scripts per phase and executes nothing. It does not create the database either, so on a
database that does not exist yet the journal read fails; the migrator logs a warning and reports every script as
pending rather than stopping.

> **Note:** The per-phase summary at the end of a run is shared with a real migration, so on a dry run it still
> reads `Executed scripts: N` — meaning *would be executed*. The `Dry run:` lines above it, and the
> `Dry run: the database will not be created and no script will be executed` line near the top, are what tell you
> nothing happened.

---

## Environment variables

| Variable | Used by | Purpose |
|----------|---------|---------|
| `TSDB_CONNECTION_STRING` | migrator | Connection string when `--connection-string` is absent |
| `TSDB_NO_BUILD` | `migrate.sh` | `1` skips the build and runs the previously built binary |
| `TSDB_CONFIGURATION` | `migrate.sh` | Build configuration, default `Release` |
| `ASPNETCORE_ENVIRONMENT` | migrator, tests | Selects `appsettings.<env>.json`; default `development`. The tests read `appsettings.integration.json` underneath it regardless |

---

## Logging

| Sink | Where | Notes |
|------|-------|-------|
| Console | stdout | Human-readable, minimum level Debug |
| File | `logs/migrator.log` | Compact JSON, rolls daily, 1 GB per file, 10 files kept |

`logs/` is created relative to the working directory and is gitignored. Passwords are masked in both sinks.

---

## Exit codes

| Code | Meaning |
|------|---------|
| `0` | Every phase applied successfully, or a dry run completed |
| `1` | Option parsing failed, or a script failed, or the target could not be resolved |

A failing script aborts the run: later phases are not attempted, the failing script's name and contents are
logged, and the exit code is `1`.

---

## Local containers

`docker-compose.yml` provides both engines for testing, on non-default ports so they do not collide with a local
installation:

| Service | Image | Ports | Credentials |
|---------|-------|-------|-------------|
| `clickhouse` | `clickhouse/clickhouse-server:26.7` | `18223:8123`, `19223:9000` | `default` / `passw0rd` |
| `postgres` | `postgres:18` | `15432:5432` | `postgres` / `passw0rd` |

```bash
docker compose up -d
```

The integration tests read their connection strings from
[`appsettings.integration.json`](../src/Migrator.Tests/appsettings.integration.json), overridable with the
`ClickHouse__ConnectionString` and `Postgres__ConnectionString` environment variables.

---

## See also

[Operations Guide](OPERATIONS_GUIDE.md) · [Template Variables](TEMPLATE_VARIABLES.md) ·
[Users & Permissions](USERS_AND_PERMISSIONS.md) · [Security](SECURITY.md)
