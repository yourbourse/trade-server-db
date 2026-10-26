<div align="center">

# Security

**What is bound, what is validated, and why the difference matters**

[Threat model](#threat-model) · [Bound values](#values-travel-as-parameters) · [Identifiers](#identifiers-are-validated) · [Passwords](#passwords-are-escaped) · [Secrets](#handling-secrets) · [Least privilege](#least-privilege)

</div>

---

## Threat model

Everything the migrator consumes is **operator input**: a connection string, a list of users, a set of
variables, and SQL files from this repository. An operator running migrations already holds DDL rights on the
target database, so this is not a privilege boundary — someone who can pass `--users` can also connect with
`psql` and do worse.

The guards exist for a narrower and much more common problem: **a value that is not shaped the way the SQL
assumed.** A password from a secret store that happens to contain a quote should either work correctly or fail
loudly. It must never silently produce different SQL from what the script author intended.

| Concern | In scope? |
|---------|-----------|
| A password containing `'`, `\`, `;` or `--` producing broken or unintended SQL | **Yes** |
| A mistyped identifier corrupting a DDL statement | **Yes** |
| Credentials leaking into logs | **Yes** |
| An operator deliberately doing damage with their own DDL rights | No — they already can |
| SQL from untrusted third parties | No — every script is reviewed in this repository |

---

## Values travel as parameters

Everything the migrator sends that is a **value** is a bound parameter, never string-concatenated:

| Statement | Bound values |
|-----------|--------------|
| Journal lookup (`information_schema.tables` / `system.tables`) | schema name, table name |
| Journal insert | script name, applied timestamp |
| Journal delete-by-prefix (clears the previous entry of a re-runnable script) | script-name prefix |
| ClickHouse database existence check (`system.databases`) | database name |

Script names come from file names on disk, and they are still bound. ClickHouse uses `{name:Type}` placeholders
rather than `@name`, which the provider's journal handles.

---

## Identifiers are validated

Neither PostgreSQL nor ClickHouse accepts a bind parameter in place of an **identifier**. Statements like
`CREATE TABLE <journal>`, `CREATE ROLE <user>`, `GRANT … TO <user>`, `create database <name>` and `ON CLUSTER
'<cluster>'` therefore have to be assembled as text — so every value that reaches one is checked first:

```
^[A-Za-z_][A-Za-z0-9_]{0,62}$
```

Start with a letter or underscore, continue with letters, digits or underscores, at most 63 characters. That is
the intersection of what both engines accept as an unquoted identifier, capped at PostgreSQL's 63-byte limit.

| Value | Source |
|-------|--------|
| `--journal-table` | command line |
| `##database_name##` | connection string |
| `##trades_table##` | default or `--variables` |
| `##daily_statements_table##` | default or `--variables` |
| `##order_snapshots_table##` | default or `--variables` |
| `##position_snapshots_table##` | default or `--variables` |
| `##cursor_table##` | default or `--variables` |
| `##ticks_table##` | default or `--variables` |
| `##positions_table##` | default or `--variables` |
| `##cluster_name##` | `--cluster` (empty passes) |
| `##user_name##` | each `--users` entry |

The names of the event logs, the tables derived from them and the views are validated by the same rule and
from the same sources — they are left out of the table only to keep it short. The complete set is in
[Template Variables → Validation](TEMPLATE_VARIABLES.md#validation).

Anything else fails the run **before a statement is built**, with an error naming the offending value and the
rule. The variable-based identifiers are validated *after* `--variables` has been applied, so an override cannot
smuggle something past the check.

```
User name 'trade-exporter' is not a valid SQL identifier. It has to start with a letter or an underscore,
continue with letters, digits or underscores, and be at most 63 characters long. Identifiers are written into
DDL as text - no database accepts a bound parameter in their place - so anything else is rejected here.
```

> **Note:** A hyphen is the usual thing that trips people up. `trade-exporter` is not a valid unquoted
> identifier on either engine; use `trade_exporter`.

---

## Passwords are escaped

`##user_password##` sits inside a quoted literal in `users/*.sql` — `IDENTIFIED WITH sha256_password BY
'##user_password##'` — so it cannot be bound. It is escaped for the dialect it is written into:

| Provider | Escaping |
|----------|----------|
| PostgreSQL | `'` → `''` |
| ClickHouse | `\` → `\\` first, then `'` → `\'` |

The backslash must be escaped **before** the quote, or the escape character introduced for the quote would
itself be doubled and the result would be wrong.

The outcome: a password containing `'`, `\`, `;` or `--` is stored intact and still authenticates. There is an
integration test that provisions a user with such a password and logs in as them, on both engines, to prove it.

Passwords are not validated beyond that — they are free-form by nature. They are, however, masked everywhere the
migrator writes: the argument dump, the console log, `logs/migrator.log`, and any error message quoting a
`--users` entry.

---

## `--variables` is trusted input

Values passed with `--variables` are substituted **verbatim and unvalidated**, because they legitimately carry
things that are not identifiers — engine names, TTL numbers, intervals, whole clauses. The single exception is
the four identifier variables above, which are re-checked after the override.

Treat the flag accordingly: it is an operator's tool, at the same trust level as the SQL files themselves. Do not
wire it to anything that accepts input from elsewhere.

---

## Handling secrets

| Practice | Reason |
|----------|--------|
| Pass the connection string as `TSDB_CONNECTION_STRING` | Keeps the password out of the process list and shell history |
| Inject `--users` passwords from a secret store | Same reason; the migrator masks its own output but not your terminal |
| Rotate by re-running the users phase | Both providers re-apply the password, so rotation is a normal migration |
| Archive `logs/migrator.log` freely | Passwords are masked in it — the keys treated as secret are `Password` and `Pwd` |
| Use a migration account, not the exporter account | The exporter has no DDL rights on purpose |

Nothing in this repository stores a credential. The values that appear in it are the throwaway container
passwords in `docker-compose.yml`, the CI service-container values in `.github/workflows/ci.yaml`, and
`db.example.com`-style placeholders in the documentation.

---

## Least privilege

The roles the `users/` phase provisions are deliberately narrow:

| Role | PostgreSQL tables | PostgreSQL cursor | ClickHouse ticks and positions | DDL | Delete |
|------|-------------------|-------------------|--------------------------------|-----|--------|
| `exporter` | `INSERT`, `SELECT` | `INSERT`, `SELECT`, `UPDATE` | `INSERT` | no | no |
| `ro` | `SELECT` | `SELECT` | `SELECT` | no | no |

The export is append-only and never rewrites or removes a record, so the exporter needs nothing more. Run
migrations with a separate account that has DDL rights, and keep the two apart —
[Users & Permissions](USERS_AND_PERMISSIONS.md) has the full grant list.

---

## Reporting a problem

If you believe you have found a way to make the migrator emit SQL its author did not intend — a value that
escapes a literal, an identifier that passes validation but changes a statement's meaning — treat it as a real
bug and report it privately to YourBourse rather than opening a public issue.

---

## See also

[Users & Permissions](USERS_AND_PERMISSIONS.md) · [Template Variables](TEMPLATE_VARIABLES.md) ·
[Configuration](CONFIGURATION.md) · [Architecture](ARCHITECTURE.md)
