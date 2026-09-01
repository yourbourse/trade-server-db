<div align="center">

# ClickHouse Provider

**The custom DbUp provider, and the ClickHouse behaviour that made it necessary**

[Why](#why-a-custom-provider) · [Statement splitting](#statement-splitting) · [Journal](#the-journal) · [Multiple result sets](#multiple-result-sets) · [Create database](#creating-the-database) · [Clusters](#cluster-support) · [Errors](#error-reporting)

</div>

---

## Why a custom provider

DbUp ships providers for SQL Server, PostgreSQL, MySQL and others; there is no usable one for ClickHouse. The
`src/DbUp.ClickHouse/` project supplies the four pieces DbUp needs, on top of the official `ClickHouse.Driver`
HTTP client:

| Piece | Type | What it does |
|-------|------|--------------|
| Connection manager | `ClickHouseConnectionManager` | Opens connections, splits scripts into statements |
| Journal | `ClickHouseTableJournal` (+ a forked `TableJournal` base) | Reads and writes `schema_versions` |
| Script executor | `ClickHouseScriptExecutor` | Runs statements, unwraps ClickHouse errors |
| Object parser | `ClickHouseObjectParser` | Quotes and unquotes identifiers |
| Preprocessor | `ClickHousePreprocessor` | Pass-through — substitution happens in the migrator |

Wired together by `ClickHouseExtensions.ClickHouseDatabase(…)`, which mirrors DbUp's own fluent style:

```csharp
DeployChanges.To.ClickHouseDatabase(connectionString, databaseName, journalTable, clusterName)
```

`src/DbUp.PSQL/` is deliberately the same shape for PostgreSQL, so the two can be read side by side.

---

## Statement splitting

**ClickHouse's HTTP interface accepts exactly one statement per request.** A script with three `CREATE`s has to
become three requests, so the connection manager overrides `SplitScriptIntoCommands`:

```csharp
Regex.Split(scriptContents, @"^[ \t]*----.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)
    .Select(x => x.Trim())
    .Where(ContainsExecutableStatement)
```

Any **whole line** of four or more dashes is a separator, so `----break----`, `----` and `---- anything ----`
all work. The convention in this repository is `----break----`.

The pattern is anchored to the line on purpose: four dashes inside a statement — `SELECT 'a----b'`, or a comment
drawing a rule across the file — must not split the command in two.

### Comment-only chunks are dropped

A file header sitting above the first separator is a chunk containing nothing but comments. ClickHouse rejects
that with *Empty query*, so such chunks are filtered out: line comments are stripped, and if nothing is left the
chunk is not sent.

That is why the ClickHouse scripts can open with a separator before their header comment — the header becomes its
own chunk and is discarded:

```sql
----break----

-- Trades exported by the trade server …
-- (never reaches the server)

CREATE TABLE IF NOT EXISTS ##trades_table## ##cluster_name_suffix## ( … );
```

Comments that *introduce* a statement are kept, because the chunk also contains executable SQL.

> **Note:** The PostgreSQL connection manager uses the same splitting logic, but a script with no separator is
> sent to the server as a single command — which keeps it inside one implicit transaction. Splitting a PostgreSQL
> script is therefore a deliberate choice, not a requirement.

---

## The journal

`ClickHouseTableJournal` keeps `schema_versions` in the same shape as the PostgreSQL one, so both providers
behave identically from the outside:

```sql
CREATE TABLE IF NOT EXISTS schema_versions ON CLUSTER '…'
(
    script_name LowCardinality(String),
    applied_at  DateTime64(6, 'UTC')
)
ENGINE = MergeTree          -- ReplicatedMergeTree when a cluster is given
PRIMARY KEY (script_name)
ORDER BY (script_name, applied_at);
```

### Re-runnable scripts

A re-runnable script's journal entry ends in `::<md5>`. On store, the journal first removes every entry whose
name starts with the part before `::`, so exactly one row per script survives:

```sql
ALTER TABLE schema_versions ON CLUSTER '…'
DELETE WHERE startsWith(script_name, {scriptNamePrefix:String})
SETTINGS mutations_sync = 2;
```

Two details matter here:

| Detail | Reason |
|--------|--------|
| `ALTER TABLE … DELETE` | ClickHouse has no row `DELETE`; this is a mutation |
| `mutations_sync = 2` | Makes the mutation finish before the replacement row is written, so a later read cannot see the entry that is on its way out |

The prefix is a script name taken from a file on disk and is still passed as a bound parameter rather than pasted
into the statement.

### Parameter syntax

ClickHouse binds with `{name:Type}` rather than `@name`. DbUp's base class hands the journal bare parameter
names, so the provider reformats them:

```sql
INSERT INTO schema_versions (script_name, applied_at)
VALUES ({scriptName:String}, CAST({applied:String} AS DateTime64(6, 'UTC')));
```

The same applies to the existence check, which queries `system.tables` with the database and table name bound.

---

## Multiple result sets

`TableJournal` is forked from DbUp's own implementation with **one** change, in `GetExecutedScripts`:

```csharp
do
{
    while (reader.Read())
    {
        scripts.Add((string)reader[0]);
    }
}
while (reader.NextResult());
```

ClickHouse can return several result sets for a single query, and DbUp's original reads only the first — which
silently truncates the list of applied scripts and makes already-applied migrations look pending. Iterating every
result set fixes it.

The file keeps a link to the upstream revision it was copied from, so the fork can be diffed if DbUp changes.

---

## Creating the database

`EnsureDatabase.For.ClickHouseDatabase(…)` connects to the `system` database, checks
`system.databases` for the target (bound parameter), and creates it when missing:

```sql
create database if not exists <name> ON CLUSTER '<cluster>';
```

Then it waits for the database to be usable — relevant on a replicated setup, where a freshly created database
can still be read-only while replicas catch up:

| Attempt | Delay before next check |
|---------|------------------------|
| 1 | 1s |
| 2 | 2s |
| 3 | 4s |
| … | doubling, up to 7 attempts |

Readiness is checked against `system.replicas`. A single instance has no replicas, so the check passes on the
first attempt. If the database is still read-only after the last attempt, the run fails with
*Database … is in readonly-mode*.

This step is skipped entirely on `--dry-run`.

---

## Cluster support

`--cluster` threads through the provider in three places:

| Place | Effect |
|-------|--------|
| Scripts | `##cluster_name_suffix##` expands to ` ON CLUSTER '<name>' ` |
| Table engines | `##merge_tree_engine##` / `##replacing_merge_tree_engine##` become their `Replicated*` variants |
| Journal | `CREATE TABLE … ON CLUSTER`, `ReplicatedMergeTree`, and `ALTER TABLE … ON CLUSTER … DELETE` |

The journal is the subtle one: without `ON CLUSTER` it would exist on the node the migrator happened to connect
to, and every other node would consider every script pending.

Engines are chosen by whether a cluster was given, because a single instance has no Keeper to coordinate
replication — `ReplicatedMergeTree` on a standalone server is a misconfiguration, not a harmless extra.

> **Warning:** The engine is fixed at `CREATE TABLE`. Adding `--cluster` to a deployment whose tables already
> exist does not convert them; that is a data migration.

---

## Error reporting

`ClickHouseScriptExecutor` catches `ClickHouseServerException` and logs what the server actually objected to
before rethrowing:

```
ClickHouse exception has occured in script: '[clickhouse] 20260315_01_add_column.sql'
Script block number: 2;
   - Error code:   47;
   - Query:        ALTER TABLE trades ADD COLUMN …;
   - Message:      Unknown identifier …
```

The block number is the index of the statement **within the script**, counted after splitting — useful when a
long script fails, since the query text alone may not identify which of several similar statements it was.

`GetVerifySchemaSql` throws `NotSupportedException`: ClickHouse has no schemas in the PostgreSQL sense — the
database is the namespace — so there is nothing to verify. The upgrade engine is configured with a `null` schema,
so that path is never reached.

---

## Known constraints

| Constraint | Consequence |
|------------|-------------|
| ClickHouse 24.x minimum | `ClickHouse.Driver` 1.3.0 sends settings older servers reject |
| No transactions | A script failing on its third statement leaves the first two applied — write ClickHouse migrations so each script is independently re-runnable |
| `Decimal` cannot hold `NaN` | An exporter sending `NaN` gets `DECIMAL_OVERFLOW`; see [Schema Reference](SCHEMA_REFERENCE.md) |
| Mutations are asynchronous | Anything relying on a mutation having completed needs `mutations_sync`, as the journal delete does |
| Access management must be enabled | The `users/` phase needs it — `CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT: 1` locally |

---

## See also

[Architecture](ARCHITECTURE.md) · [Writing Migrations](WRITING_MIGRATIONS.md) ·
[Schema Reference](SCHEMA_REFERENCE.md) · [Troubleshooting](TROUBLESHOOTING.md)
