<div align="center">

# Writing Migrations

**How to add a script that is safe to apply, and safe to apply again**

[The five phases](#the-five-phases) · [Incremental](#incremental-scripts) · [Re-runnable](#re-runnable-scripts) · [Separators](#statement-separators) · [Variables](#using-template-variables) · [Both engines](#writing-for-both-engines) · [Checklist](#checklist)

</div>

---

## The five phases

Each provider folder — `psql/` and `clickhouse/` — holds the same five phases, applied in this order:

| Order | Folder | Re-runnable | Journal name | Purpose |
|-------|--------|-------------|--------------|---------|
| 1 | `pre/` | yes | `[psql/pre] x.sql::md5` | Anything that must exist before the tables. Optional — create it when you need it |
| 2 | *folder root* | **no** | `[psql] x.sql` | Incremental migrations, applied once in file-name order |
| 3 | `views/` | yes | `[psql/views] x.sql::md5` | Views and materialized views — `trades_history` and `cash_transfers` live here on PostgreSQL; ClickHouse has none |
| 4 | `users/` | yes | `[psql/users] x.sql for <user>::md5` | Roles and users, once per matching `--users` entry |
| 5 | `post/` | yes | `[psql/post] x.sql::md5` | Everything that has to run after the schema, such as TTLs |

A phase whose folder is absent is skipped. A failing script aborts the whole run — later phases are not
attempted.

Within a phase, files are applied in file-name order.

---

## Incremental scripts

Scripts in the folder root run **once, ever**. They are recorded in `schema_versions` under their file name and
never looked at again.

**Naming:** `YYYYMMDD_NN_what_it_does.sql`

```
psql/20260315_01_add_broker_id.sql
clickhouse/20260315_01_add_broker_id.sql
```

The date orders the file within the phase and the `NN` orders same-day scripts. Use the date you write the
script, not the date you expect to deploy it.

> **Warning:** **Never edit a script that has already been applied anywhere.** It will not run again, so the file
> and the deployed database drift apart silently. Add a new script instead.

Because these run exactly once, they are the right place for anything destructive or non-repeatable:
`ALTER TABLE … ADD COLUMN`, a backfill `UPDATE`, a rename.

Still, prefer statements that tolerate being re-entered — `IF NOT EXISTS` throughout, as the existing scripts
do. A migration may have to be re-run after a partial ClickHouse failure, or applied to a database that was
provisioned by hand from the *Export Setup* guide before the migrator existed.

---

## Re-runnable scripts

Scripts in `pre/`, `views/`, `users/` and `post/` are journalled with the MD5 of their **processed** content
(after template substitution) appended to the name. Before the new entry is written, every previous entry for
that file is deleted, so the journal holds exactly one row per script and the script re-runs whenever its
content changes — and only then.

That has two consequences worth internalising:

- **A re-runnable script must be idempotent.** It will run again, possibly many times. `CREATE OR REPLACE VIEW`,
  `CREATE … IF NOT EXISTS`, `ALTER … ` that converges on a target state — all fine. A bare `INSERT` is not.
  `CREATE OR REPLACE VIEW` is the right idiom for a view because it keeps the grants the `users/` phase applied,
  but on PostgreSQL it can only *append* a column: renaming, reordering or dropping one needs an incremental
  script that `DROP`s the view first, and the run carrying it has to pass `--users` so the grants come back.
- **Changing a variable changes the hash.** Renaming the trades table with `--variables` re-runs every
  re-runnable script that mentions `##trades_table##`, because their processed content differs.

`--force` re-applies all of them regardless of hash, for when the *effect* was lost without the *content*
changing — someone dropped a view by hand.

---

## Statement separators

A whole line of four or more dashes splits a script into statements:

```sql
----break----
```

The separator has to be a line of its own — four dashes inside a statement, as in `SELECT 'a----b'`, are left
alone.

The rules differ by engine:

| Engine | Separator needed? | Why |
|--------|-------------------|-----|
| **ClickHouse** | **Yes, between every statement** | The HTTP interface accepts exactly one statement per request |
| **PostgreSQL** | No | Npgsql sends a whole file as one command, inside one implicit transaction |

A PostgreSQL script needs no separator, and adding one buys less than it looks like it does.

> **Warning:** **A separator does not escape the transaction.** The migrator applies every PostgreSQL script with
> `WithTransactionPerScript()`, so splitting a file produces several commands *inside the same transaction*, not
> several independent ones. `CREATE INDEX CONCURRENTLY` therefore fails here whether or not the script is split:
>
> ```
> 25001: CREATE INDEX CONCURRENTLY cannot run inside a transaction block
> ```
>
> Build such an index by hand outside the migrator and let the script's `IF NOT EXISTS` adopt it — see
> [`psql/20260828_01_add_reporting_indexes.sql`](../psql/20260828_01_add_reporting_indexes.sql), which documents
> the routine. Making the migrator support non-transactional scripts would mean running them through a second
> upgrade engine built `WithoutTransaction()`; nothing needs it yet.

A chunk containing nothing but comments is dropped rather than sent to the server, which is why the ClickHouse
scripts can open with a `----break----` above their file header comment: the header becomes its own chunk and is
discarded instead of arriving at the server as an empty query.

```sql
----break----

-- This header is a comment-only chunk. It never reaches the server.

CREATE TABLE IF NOT EXISTS ##database_name##.##ticks_table## ##cluster_name_suffix##
(
    ...
)
ENGINE = ##replacing_merge_tree_engine##;
```

---

## Using template variables

Write `##trades_table##`, never `trades`, and `##ticks_table##`, never `ticks`. Every exported table name is
configurable in the trade server's export configuration, so a deployment that renamed one passes the name with
`--variables` and the scripts follow.

The variables that are always available:

| Variable | Value |
|----------|-------|
| `##database_name##` | Database from the connection string |
| `##trades_table##` | `trades` unless overridden |
| `##daily_statements_table##` | `daily_statements_export` unless overridden |
| `##order_snapshots_table##` | `daily_statements_order_snapshots` unless overridden |
| `##position_snapshots_table##` | `daily_statements_position_snapshots` unless overridden |
| `##cursor_table##` | `export_cursor` unless overridden |
| `##ticks_table##` | `ticks` unless overridden |
| `##trades_history_view##` | `trades_history` unless overridden |
| `##cash_transfers_view##` | `cash_transfers` unless overridden |
| `##assembly_version##` | Version of the migrator applying the script |

ClickHouse adds the cluster and engine variables; the `users/` phase adds `##user_name##` and
`##user_password##`. The full list, with validation rules, is in [Template Variables](TEMPLATE_VARIABLES.md).

> **Note:** A `##placeholder##` that the migrator cannot resolve **fails the run before the script reaches the
> database**, naming the placeholder and listing what is available. That is deliberate: an unresolved `##` would
> otherwise travel to the server and come back as a syntax error pointing at a character offset.

Anything else you need is passed with `--variables key=value` — and remember those values are substituted
verbatim without validation, so treat that flag as trusted operator input.

---

## Writing for both engines

> **Important:** The two folders create **different tables**, because the trade server exports each entity to
> exactly one engine: trades, daily statements, their snapshots and the export cursor go to PostgreSQL, ticks go
> to ClickHouse. A change to a table definition therefore lands in one folder only — `psql/` for the five
> PostgreSQL tables and the views over them, `clickhouse/` for the ticks table — and has nothing to mirror.
>
> What does span both folders is the `users/` and `post/` phases: a new role, or a change to what an existing
> one may do, is written for both engines in their own dialects.

Where a change does span both folders, keep them parallel — same file name in each — and remember the engines
differ:

| Concern | PostgreSQL | ClickHouse |
|---------|------------|------------|
| Uniqueness | unique index | none — rely on the `ReplacingMergeTree` sorting key |
| Identity columns | `GENERATED ALWAYS AS IDENTITY` | no equivalent; the sorting key identifies the row |
| `UPDATE` | normal | `ALTER TABLE … UPDATE` mutation, asynchronous |
| Transactions | per script | none |
| `NaN` | fine in `NUMERIC` | **rejected** by `Decimal` — `DECIMAL_OVERFLOW` |
| Cluster DDL | n/a | needs `##cluster_name_suffix##` on every DDL statement |

A change that genuinely applies to one engine only — a ClickHouse TTL, a PostgreSQL partial index — belongs in
that folder alone. Say so in a comment at the top of the file, so the next person does not "fix" the asymmetry.

---

## Checklist

Before opening a PR with a new script:

- [ ] Right phase — incremental in the folder root, idempotent things in `views/` / `post/`
- [ ] Named `YYYYMMDD_NN_what_it_does.sql` if incremental
- [ ] The table variables (`##trades_table##`, `##daily_statements_table##`, `##order_snapshots_table##`,
      `##position_snapshots_table##`, `##cursor_table##`, `##ticks_table##`) rather than hard-coded names
- [ ] `IF NOT EXISTS` / `CREATE OR REPLACE` wherever the engine allows it
- [ ] ClickHouse: a `----break----` between every statement, and `##cluster_name_suffix##` on every DDL statement
- [ ] The view variables (`##trades_history_view##`, `##cash_transfers_view##`) rather than hard-coded names
- [ ] Mirrored in the other provider's folder when it touches `users/` or `post/` — a table definition belongs
      to the engine that holds the table and has no mirror
- [ ] `--dry-run` lists it, and only it
- [ ] Applied twice: the second run reports `Executed scripts: 0`
- [ ] Integration tests pass against both engines

```bash
./migrate.sh --provider postgres --connection-string "Host=localhost;Port=15432;Database=trade_demo;Username=postgres;Password=passw0rd" --users "Access=exporter;User=trade_exporter;Password=passw0rd" --dry-run
```

---

## See also

[Template Variables](TEMPLATE_VARIABLES.md) · [Schema Reference](SCHEMA_REFERENCE.md) ·
[Architecture](ARCHITECTURE.md) · [Contributing](CONTRIBUTING.md)
