<div align="center">

# Operations Guide

**Running the migrator against a database that matters**

[First provisioning](#first-provisioning) · [Routine upgrade](#routine-upgrade) · [Reading the output](#reading-the-output) · [ClickHouse clusters](#clickhouse-clusters) · [Pipelines](#running-from-a-pipeline) · [Recovery](#when-something-goes-wrong)

</div>

---

## Before you start

Three properties of this tool shape every procedure below:

| Property | Consequence |
|----------|-------------|
| **No rollback** | The migrator only rolls forward. A bad script is corrected by a new script, never by undoing the old one |
| **Idempotent by design** | Re-running applies nothing when nothing changed, so it is safe in an unattended pipeline |
| **Fails fast** | The first failing script aborts the run; later phases are not attempted |

> **Warning:** PostgreSQL DDL is transactional and each script runs in its own transaction, so a failing
> PostgreSQL script leaves nothing half-applied. **ClickHouse has no equivalent** — a script that fails on its
> third statement leaves the first two in place. Split ClickHouse migrations so each script is independently
> re-runnable.

---

## First provisioning

The order matters: schema first, users second, and the export pipeline only after both.

> **Important:** On **ClickHouse**, `trades` and `export_cursor` are not created by the migrator — the
> deployment provisions them. Create them before starting the export; the migration itself succeeds without
> them, because ClickHouse grants on a table by name whether or not it exists. On PostgreSQL the migrator
> creates both. See [Schema Reference](SCHEMA_REFERENCE.md#overview).

**1. Confirm the target.** The connection string must name the database. The account needs DDL rights, and
`CREATEDB` (PostgreSQL) or the equivalent ClickHouse grant if the database does not exist yet.

**2. Dry run.**

```bash
export TSDB_CONNECTION_STRING="Host=db.example.com;Port=5432;Database=trading;Username=migrator;Password=..."
```

```bash
./migrate.sh --provider postgres --dry-run
```

Expect every script to be listed as pending, and a warning that the journal could not be read — the database
does not exist yet, and a dry run is not allowed to create it.

**3. Apply.**

```bash
./migrate.sh --provider postgres --users "Access=exporter;User=trade_exporter;Password=..." "Access=ro;User=trade_reader;Password=..."
```

**4. Verify.** `schema_versions` exists, both roles exist, and a second run reports
`Executed scripts: 0`.

**5. Hand the exporter credentials to the trade server** and start the export.

> **Note:** Use a dedicated migration account, not the exporter account. The exporter deliberately has no DDL
> rights — that is the point of the `exporter` role.

---

## Routine upgrade

When a trade server release extends the export schema, the procedure is the same three commands every time:

```bash
git pull
```

```bash
./migrate.sh --provider postgres --dry-run
```

```bash
./migrate.sh --provider postgres
```

The dry run is the review step: it lists exactly the scripts that are new since the last run. If that list
contains something you were not expecting, stop and read the script before applying it.

Pass `--users` again on every run if you want the role definitions kept in step — the `users/` scripts are
re-runnable and also rotate the password (see [Users & Permissions](USERS_AND_PERMISSIONS.md)). Omitting
`--users` simply skips the phase; it does not remove anything.

### Both engines

There is no combined mode: run the migrator once per engine, each with its own provider and connection string.

```bash
./migrate.sh --provider postgres   --connection-string "Host=pg;Port=5432;Database=trading;Username=migrator;Password=..."
```

```bash
./migrate.sh --provider clickhouse --connection-string "Host=ch;Port=8123;Database=trading;Username=migrator;Password=..."
```

---

## Reading the output

A run narrates every phase. The parts worth knowing:

```
Migration arguments:
 --provider postgres
 --connection-string Host=db.example.com;Port=5432;Database=trading;Username=migrator;Password=*******
 ...
```

The argument dump repeats what the migrator understood, with passwords masked. Check `--provider` and the
database here if a run does something surprising.

```
====================================
Migrating Postgres database trading, trades table: trades, cursor table: export_cursor, users: [trade_exporter(exporter)]
```

The target line reports the **effective** values — after `--variables` overrides — so this is where you confirm
that a renamed trades table was actually picked up.

```
====================================
Running migration: (root)
...
Scripts path: (root)
Executed scripts: 2
 - [psql] 20260101_01_create_trades.sql
 - [psql] 20260101_02_create_export_cursor.sql
```

One block per phase, `(root)` being the incremental phase. `No files for views` means the phase had nothing to
do. On the second run the counts are `0` everywhere except, if you passed `--force`, the re-runnable phases whose
inputs you supplied.

> **Note:** On a `--dry-run` this same summary reads `Executed scripts: N`, meaning *would be executed*. Trust
> the `Dry run:` lines, not the summary wording.

A failure ends with the script name, the server error and the full script contents, then a non-zero exit:

```
Exception: ...
ErrorScript: [psql] 20260315_01_add_broker_id.sql - ALTER TABLE ...
Migration failed
```

Everything also lands in `logs/migrator.log` as compact JSON, rolling daily, ten files kept.

---

## ClickHouse clusters

On a replicated setup, pass `--cluster` on **every** run — it is not recorded anywhere, so a run without it
would create local, unreplicated objects next to the replicated ones:

```bash
./migrate.sh --provider clickhouse --cluster my_cluster --connection-string "Host=ch-1;Port=8123;Database=trading;Username=migrator;Password=..."
```

| What changes | Effect |
|--------------|--------|
| Statements | `ON CLUSTER 'my_cluster'` — DDL propagates to every node |
| Table engines | `ReplicatedMergeTree`, `ReplicatedReplacingMergeTree` |
| Journal | `ReplicatedMergeTree`, so every node agrees on what has been applied |

Point the connection string at one node; the cluster clause distributes the DDL. Every ClickHouse run checks
that the database is not in read-only mode before applying anything, retrying with a doubling delay for up to
seven attempts — on a single instance that check passes immediately, and on a replicated setup this is where a
run waits for replicas to catch up.

> **Warning:** Switching an existing single-instance deployment to `--cluster` does **not** convert the tables
> that already exist. The engine is baked in at `CREATE TABLE`. Converting a live deployment is a data migration,
> not a flag change.

---

## Running from a pipeline

The migrator is built for unattended use: it is idempotent, it exits non-zero on the first failure, and it never
prompts.

```bash
TSDB_CONNECTION_STRING="$DEPLOY_DB_CONNECTION" ./migrate.sh --provider postgres
```

Recommendations:

| Practice | Why |
|----------|-----|
| Inject the connection string from the secret store as `TSDB_CONNECTION_STRING` | Keeps it out of process arguments and shell history |
| Run the migration **before** deploying the code that needs the new schema | The export is append-only; a new column has to exist before the exporter writes it |
| Fail the deployment on a non-zero exit | A half-migrated schema should never be followed by a code deploy |
| Archive `logs/migrator.log` as a build artefact | It is the record of what was applied and when |
| Set `TSDB_NO_BUILD=1` when the binary is already built | Skips the rebuild in a two-stage pipeline |

### Deploying a self-contained copy

A pipeline that should not clone this repository on every run can publish the migrator once and ship the output:

```bash
dotnet publish src/Migrator/Migrator.csproj -c Release -o /opt/trade-server-db-migrator
```

```bash
dotnet /opt/trade-server-db-migrator/Migrator.dll --provider postgres --dry-run
```

The publish output carries `psql/` and `clickhouse/` next to `Migrator.dll`, so the same invocation works with no
checkout — the runner falls back to the folders beside the binary when the working directory has none. Editing
the SQL then means republishing, which is the trade-off for not needing a checkout.

---

## When something goes wrong

### A script failed halfway

1. Read the error and the script contents from the log — both are printed in full.
2. Fix the **script**, not the database, unless the database is in a state the script cannot re-enter.
3. Re-run. Incremental scripts that already succeeded are journalled and will not run again.

On PostgreSQL the failing script rolled back cleanly. On ClickHouse, check what its earlier statements already
did before re-running.

### A migration was applied that should not have been

There is no rollback. Write a new incremental script that corrects the state, and never edit the script that was
already applied — an incremental script will not run a second time, so editing it only makes the file disagree
with the deployed database.

### A view or role was changed by hand

Re-run with `--force`. Every re-runnable script is applied again regardless of its hash, restoring the state the
files describe.

### The journal looks wrong

`schema_versions` is a plain table. Read it:

```sql
SELECT script_name, applied_at FROM schema_versions ORDER BY applied_at;
```

Editing it by hand is a last resort, and always in the direction of "this script really was applied" — deleting
a row makes an incremental script run again, which is only safe if the script is genuinely re-runnable.

---

## See also

[Configuration](CONFIGURATION.md) · [Troubleshooting](TROUBLESHOOTING.md) ·
[Users & Permissions](USERS_AND_PERMISSIONS.md) · [Writing Migrations](WRITING_MIGRATIONS.md)
