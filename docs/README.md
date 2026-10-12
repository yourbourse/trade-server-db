<div align="center">

# Documentation

**Everything about the schema, the migrator and the scripts it applies**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-4169E1?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![ClickHouse](https://img.shields.io/badge/ClickHouse-24.x%2B-FFCC01?logo=clickhouse&logoColor=black)](https://clickhouse.com/)
[![DbUp](https://img.shields.io/badge/DbUp-6.0-2C3E50)](https://dbup.readthedocs.io/)
![License](https://img.shields.io/badge/License-Proprietary-red.svg)

[Getting Started](GETTING_STARTED.md) · [Quick Start](QUICKSTART.md) · [Configuration](CONFIGURATION.md) · [Schema](SCHEMA_REFERENCE.md) · [Architecture](ARCHITECTURE.md)

</div>

---

## Start Here

New to this repository? Read in this order:

1. **[Getting Started](GETTING_STARTED.md)** — prerequisites, first migration against a throwaway database, and how to tell it worked
2. **[Operations Guide](OPERATIONS_GUIDE.md)** — how to run it against a real export database, and what to do at every trade server upgrade
3. **[Schema Reference](SCHEMA_REFERENCE.md)** — the tables the migrator creates and what each column means

Already know your way around? **[Quick Start](QUICKSTART.md)** is two commands.

> **Before anything else:** `migrate.sh` compiles the migrator before running it, so it needs the **.NET SDK
> 10.0.301+** — not just the runtime. Check with `dotnet --list-sdks`; install it per
> [Getting Started](GETTING_STARTED.md#install-the-net-sdk).

---

## Setup & Configuration

| Guide | Description |
|-------|-------------|
| [Getting Started](GETTING_STARTED.md) | Step-by-step first run: prerequisites, containers, first migration, verification |
| [Quick Start](QUICKSTART.md) | Minimal path for people who already know what a migrator does |
| [Configuration](CONFIGURATION.md) | Every command line option, the environment variables, exit codes, and the local container setup |

---

## Usage Guides

| Guide | Description |
|-------|-------------|
| [Operations Guide](OPERATIONS_GUIDE.md) | Running against a live export database, upgrade routine, ClickHouse clusters, CI/CD, and what the log tells you |
| [Writing Migrations](WRITING_MIGRATIONS.md) | The five phases, incremental vs re-runnable scripts, naming, statement separators, and the rules that keep a script safe to re-run |
| [Users & Permissions](USERS_AND_PERMISSIONS.md) | The `exporter` and `ro` roles, the grants each one gets on both engines, and password rotation |

---

## Reference

| Reference | Description |
|-----------|-------------|
| [Schema Reference](SCHEMA_REFERENCE.md) | The PostgreSQL trade, daily statement, snapshot, cursor and event log tables, the tables derived from the logs and the views, the ClickHouse ticks table and the journal, column by column |
| [Template Variables](TEMPLATE_VARIABLES.md) | Every `##variable##` the migrator substitutes, where its value comes from, and how it is validated |
| [Security](SECURITY.md) | What is bound, what is validated, how passwords are escaped, and the threat model behind those choices |

---

## Specifications

What a piece of behaviour has to do, and why it was built the way it was. A spec is the contract — it outlives
the implementation and is what the tests are written against; the design beside it records the decisions and
the alternatives that were rejected.

| Specification | Description |
|---------------|-------------|
| [Trading accounts — SPEC](specs/trading-accounts/SPEC.md) | The trading account event log and the current-state table derived from it by trigger: workflows, edge cases and invariants |
| [Trading accounts — design](specs/trading-accounts/design.md) | How the projection works, the decisions behind it, and what the backend team still has to confirm |

The trading accounts specification is verified by `TradingAccountsProjectionIntegrationTests`, where each test
is named for the SPEC clause it covers, so the suite reads as a conformance list.

The **client**, **symbol** and **group** event logs and their projections are built to the same contract against
a different entity: the same four event types, the same resync markers, the same end-marker sweep and the same
reconcile pass. There is no separate SPEC for them — read the one above, substituting the entity for *trading
account* — and each is verified by its own suite, which mirrors the trading account one case for case. Where
each differs is recorded in the Schema Reference; in every case the column set, whose order and types are fixed
by the schema agreed with the trade server team, plus:

| Entity | Suite | Schema Reference | Also differs in |
|--------|-------|------------------|-----------------|
| Client | `ClientsProjectionIntegrationTests` | [client_events](SCHEMA_REFERENCE.md#client_events), [clients](SCHEMA_REFERENCE.md#clients) | — |
| Symbol | `SymbolsProjectionIntegrationTests` | [symbol_events](SCHEMA_REFERENCE.md#symbol_events), [symbols](SCHEMA_REFERENCE.md#symbols) | No unique key on the symbol name |
| Group | `GroupsProjectionIntegrationTests` | [group_events](SCHEMA_REFERENCE.md#group_events), [groups](SCHEMA_REFERENCE.md#groups) | The one nullable column, `email_service_id`; no unique key on the group name |

---

## Architecture & Development

| Document | Description |
|----------|-------------|
| [Architecture](ARCHITECTURE.md) | Components, the execution pipeline end to end, journalling, and where the two providers differ |
| [ClickHouse Provider](CLICKHOUSE_PROVIDER.md) | The custom DbUp provider: statement splitting, journal, cluster handling, and the driver quirks it works around |
| [Development](DEVELOPMENT.md) | Building, the test suites, project layout, and how to add a script or a provider |
| [Contributing](CONTRIBUTING.md) | Branch and commit conventions, the review checklist, and what a good migration PR looks like |
| [Troubleshooting](TROUBLESHOOTING.md) | Errors you will actually hit, what causes them, and how to clear them |
| [FAQ](FAQ.md) | Short answers to the questions that come up most |

---

## Quick Reference

### Commands

| Command | Description |
|---------|-------------|
| `./migrate.sh --provider postgres --connection-string "…" --users "…"` | Apply the PostgreSQL schema |
| `./migrate.sh --provider clickhouse --connection-string "…" --users "…"` | Apply the ClickHouse schema |
| `./migrate.sh … --dry-run` | Report what would be applied, touch nothing |
| `./migrate.sh … --force` | Re-apply every re-runnable script even if unchanged |
| `dotnet build TradeServerDb.slnx` | Build the solution |
| `docker compose up -d` | Start local ClickHouse and PostgreSQL for testing |

### Layout

| Path | Purpose |
|------|---------|
| `psql/` | PostgreSQL scripts - root is incremental, `views/` `users/` `post/` are re-runnable |
| `clickhouse/` | ClickHouse scripts - root is incremental (the ticks table), `users/` `post/` are re-runnable |
| `src/Migrator/` | The CLI: option parsing, target resolution, phase orchestration |
| `src/DbUp.PSQL/` | DbUp provider for PostgreSQL |
| `src/DbUp.ClickHouse/` | DbUp provider for ClickHouse |
| `src/Migrator.Tests/` | Unit and integration tests |
| `migrate.sh` | Build-and-run wrapper over the CLI |

### Defaults

| Thing | Default |
|-------|---------|
| Journal table | `schema_versions` |
| Trades table | `trades` |
| Daily statements table | `daily_statements_export` |
| Order snapshots table | `daily_statements_order_snapshots` |
| Position snapshots table | `daily_statements_position_snapshots` |
| Cursor table | `export_cursor` |
| Ticks table | `ticks` |
| Script folder | `psql` or `clickhouse`, after the provider |
| Connection string | `$TSDB_CONNECTION_STRING` when `--connection-string` is absent |
| Log file | `logs/migrator.log`, rolling daily, 10 files kept |
