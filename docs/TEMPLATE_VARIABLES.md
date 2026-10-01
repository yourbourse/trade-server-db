<div align="center">

# Template Variables

**Every `##name##` the migrator substitutes, and where its value comes from**

[Always available](#always-available) · [ClickHouse only](#clickhouse-only) · [Users phase](#users-phase-only) · [Custom](#custom-variables) · [Validation](#validation) · [Unresolved](#unresolved-placeholders)

</div>

---

## How substitution works

Every script is preprocessed before it is sent to the database: each `##name##` is replaced with its value, and
the result is hashed to produce the journal entry for re-runnable scripts. Substitution is plain text
replacement — there is no expression syntax, no conditionals, no nesting.

The order in which values are decided:

```
1. derived values      database_name, the table names, assembly_version, cluster/engine variables
2. --variables         key=value pairs, applied last — they override anything above
3. users phase         user_name and user_password, added per --users entry
4. validation          the identifier variables are checked, after the overrides
5. substitution        ##name## → value, then any leftover ##...## fails the run
```

---

## Always available

| Variable | Value | Example |
|----------|-------|---------|
| `##database_name##` | The database from the connection string | `trading` |
| `##trades_table##` | `trades`, unless `--variables trades_table=…` overrides it | `trades` |
| `##daily_statements_table##` | `daily_statements_export`, unless overridden | `daily_statements_export` |
| `##order_snapshots_table##` | `daily_statements_order_snapshots`, unless overridden | `daily_statements_order_snapshots` |
| `##position_snapshots_table##` | `daily_statements_position_snapshots`, unless overridden | `daily_statements_position_snapshots` |
| `##cursor_table##` | `export_cursor`, unless `--variables cursor_table=…` overrides it | `export_cursor` |
| `##trading_account_events_table##` | `trading_account_events`, unless overridden | `trading_account_events` |
| `##trading_accounts_table##` | `trading_accounts`, unless overridden | `trading_accounts` |
| `##ticks_table##` | `ticks`, unless `--variables ticks_table=…` overrides it | `ticks` |
| `##trades_history_view##` | `trades_history`, unless overridden | `trades_history` |
| `##cash_transfers_view##` | `cash_transfers`, unless overridden | `cash_transfers` |
| `##trading_accounts_resync_view##` | `trading_accounts_resync`, unless overridden | `trading_accounts_resync` |
| `##assembly_version##` | Version of the migrator binary applying the script | `1.0.0.0` |

The table names are templated because the trade server's export configuration makes them configurable —
configurable —
each export pipeline names its own target table, and a deployment that renamed one passes the name on the command
line so the scripts follow. Use the variables instead of hard-coded names in any script that touches those
tables. The first five name PostgreSQL tables and `##ticks_table##` names the ClickHouse one; all six are
defined for both providers, so a script does not have to care which engine it runs on.

```bash
./migrate.sh --provider postgres --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=..." --variables trades_table=trades_export cursor_table=trades_export_cursor
```

The view names, and `##trading_accounts_table##`, are templated for a different reason: the trade server never
writes to any of them and so has no say in their names, but a database that already owns an object called
`trades_history`, `cash_transfers`, `trading_accounts` or `trading_accounts_resync` needs a way to move ours
aside. `##trading_accounts_table##` is a real table rather than a view — it is derived from
`##trading_account_events_table##` by a trigger, not written by the export.

> **Note:** Changing a table name changes the processed content of every re-runnable script that mentions it, so
> its MD5 changes and the script re-runs. That is the intended behaviour, not a side effect.

---

## ClickHouse only

These four are defined only when the provider is ClickHouse. Using them in a PostgreSQL script fails the run as
an unresolved placeholder.

| Variable | Without `--cluster` | With `--cluster my_cluster` |
|----------|---------------------|------------------------------|
| `##cluster_name##` | `` (empty) | `my_cluster` |
| `##cluster_name_suffix##` | `` (empty) | ` ON CLUSTER 'my_cluster' ` |
| `##merge_tree_engine##` | `MergeTree` | `ReplicatedMergeTree` |
| `##replacing_merge_tree_engine##` | `ReplacingMergeTree` | `ReplicatedReplacingMergeTree` |

`##cluster_name_suffix##` is the one to put in DDL — it carries its own surrounding spaces and the quotes around
the cluster name, and collapses to nothing on a single instance:

```sql
CREATE TABLE IF NOT EXISTS ##database_name##.##ticks_table## ##cluster_name_suffix##
(
    ...
)
ENGINE = ##replacing_merge_tree_engine##;
```

The engine variables exist because a single instance has no Keeper to coordinate replication, so the
`Replicated*` engines are only correct when a cluster was actually requested. One set of scripts therefore serves
both topologies.

`##cluster_name##` — the bare name, no `ON CLUSTER`, no quotes — is for the rare statement that needs the name
as a value, such as the deployment banner in `clickhouse/post/run_on_every_build.sql`.

---

## Users phase only

Added per `--users` entry while `users/*.sql` is processed, and available nowhere else:

| Variable | Value | Treatment |
|----------|-------|-----------|
| `##user_name##` | The `User=` value | Validated as an identifier |
| `##user_password##` | The `Password=` value | Escaped for the target dialect |

The name lands in `CREATE ROLE` and `GRANT`, where no database accepts a bound parameter, so it must be a plain
identifier. The password sits inside a quoted literal in the script — `'##user_password##'` — and is escaped for
the dialect it is written into rather than bound. See [Security](SECURITY.md) for the exact rules.

---

## Custom variables

Anything not in the tables above is passed with `--variables`:

```bash
./migrate.sh --provider clickhouse --connection-string "..." --users "Access=exporter;User=trade_exporter;Password=..." --variables ttl_days=90 retention_policy=hot_cold
```

```sql
ALTER TABLE ##database_name##.##ticks_table## ##cluster_name_suffix##
MODIFY TTL toDateTime(created_time) + INTERVAL ##ttl_days## DAY;
```

> **Warning:** `--variables` values are substituted **verbatim and unvalidated**, because they legitimately carry
> things that are not identifiers — engine names, TTL numbers, intervals. Treat the flag as trusted operator
> input; see the threat model in [Security](SECURITY.md).

`--variables` is applied last, so it can also override a derived value. Overriding `database_name` overrides only
the *variable*, not the database the migrator connects to — that always comes from the connection string.

---

## Validation

Some variables name a database object and therefore end up in DDL as text. They are validated **after**
`--variables` has had its say, so an override that is not an identifier still fails:

| Variable | Rule |
|----------|------|
| `##database_name##` | `^[A-Za-z_][A-Za-z0-9_]{0,62}$` |
| `##trades_table##` | same |
| `##daily_statements_table##` | same |
| `##order_snapshots_table##` | same |
| `##position_snapshots_table##` | same |
| `##cursor_table##` | same |
| `##ticks_table##` | same |
| `##trades_history_view##` | same |
| `##cash_transfers_view##` | same |
| `##cluster_name##` | same, when not empty |
| `##user_name##` | same |

An empty value passes — that is how `##cluster_name##` collapses on a single instance.

Everything else, including every custom `--variables` value and `##assembly_version##`, is substituted as-is.

---

## Unresolved placeholders

A script containing a `##placeholder##` that is not defined is **rejected before it reaches the database**:

```
20260315_01_add_index.sql uses ##index_name##, which the migrator does not know about.
Known variables: assembly_version, cursor_table, daily_statements_table, database_name, order_snapshots_table,
position_snapshots_table, ticks_table, trades_table.
Pass any other value with --variables name=value.
```

The check runs on the processed content, matching `##[A-Za-z0-9_]+##`, and reports each distinct placeholder
once. It exists because an unresolved `##` otherwise travels to the server verbatim and comes back as a syntax
error pointing at a character offset — technically accurate, useless in practice.

Two consequences:

- **You cannot have a literal `##something##` in a script.** If you need those characters, pass them through a
  variable.
- **A ClickHouse-only variable in a PostgreSQL script fails**, which is the intended safety net.

---

## See also

[Writing Migrations](WRITING_MIGRATIONS.md) · [Configuration](CONFIGURATION.md) · [Security](SECURITY.md)
