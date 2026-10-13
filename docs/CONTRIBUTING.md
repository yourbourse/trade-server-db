<div align="center">

# Contributing

**Branches, commits, pull requests and what reviewers look for**

[Workflow](#workflow) · [Branches](#branch-names) · [Commits](#commit-messages) · [Pull requests](#pull-requests) · [Review checklist](#review-checklist) · [Migration PRs](#what-a-good-migration-pr-looks-like)

</div>

---

## Workflow

```bash
git checkout -b TS-1234-add-broker-id-to-trades
```

1. Make the change — SQL, code, or both.
2. Build and run the unit tests. Run the integration tests if you touched SQL, a provider, or the runner.
3. Update the documentation in `docs/` in the same PR. A flag or variable that is not documented does not exist
   as far as an operator is concerned.
4. Open a PR against `master` and fill in every section of the description.

`master` is the deployable branch. There is no develop branch and no release branch.

---

## Branch names

```
TS-<ticket>-<short-kebab-description>
```

| Example | |
|---------|---|
| `TS-2646-build-psql-click-house-standalone-db-migrator` | |
| `TS-1234-add-broker-id-to-trades` | |

The JIRA ticket comes first so the branch, the commits and the PR all tie back to the same task. Work with no
ticket still needs one — if it is worth a branch, it is worth a ticket.

---

## Commit messages

```
TS-2646 Initial changes for Migrator
```

Ticket, space, then a short imperative summary. Keep the subject under ~72 characters. A body is welcome for
anything non-obvious: explain **why**, not what the diff already shows.

- One logical change per commit where practical. A migration plus the doc update for it is one logical change.
- Do not mix a refactor with a behaviour change in the same commit — reviewers cannot tell them apart.

---

## Pull requests

The trade server repositories share a PR description structure. This repository has no template file of its own
yet, so write these sections by hand:

| Section | What goes in it |
|---------|-----------------|
| **Summary** | `TS-1234 Short headline`, matching the ticket |
| **Release Notes** | A user-facing sentence for the public changelog, or `N/A` for internal changes. **Breaking changes must always be mentioned**, even for internal PRs |
| **Description** | Why the change was made and how the pieces fit. Do not list changed files — reviewers can read the diff |
| **Related Pull Requests** | Companion PRs with a genuine dependency, or `N/A` |
| **QA Section** | How to verify it **in a running system**: prerequisites, steps, expected outcome, and which existing flows need retesting |

Label the PR: `bug`, `feature`, `documentation`, `dependencies`, `infra` or `skip-release`.

Because this repository is written to be handed to clients, the QA section should include verifying from a clean
clone following only the documentation — anything that needs internal knowledge is a documentation bug.

---

## Review checklist

What a reviewer will check, and therefore what to check yourself first.

**Correctness**

- [ ] An incremental script is genuinely new — no already-applied script was edited
- [ ] Re-runnable scripts are idempotent
- [ ] The table variables (`##trades_table##`, `##daily_statements_table##`, `##order_snapshots_table##`,
      `##position_snapshots_table##`, `##cursor_table##`, `##ticks_table##`) used instead of literal names
- [ ] ClickHouse: `----break----` between statements, `##cluster_name_suffix##` on every DDL statement
- [ ] ClickHouse: the script is safe to re-enter after failing partway — there are no transactions
- [ ] New identifiers pass the identifier rule, and new free-form values are escaped or bound

**Parity**

- [ ] A `users/` or `post/` change is mirrored in the other provider's folder, or a comment explains why not —
      a table definition belongs to the engine that holds the table and has no mirror
- [ ] A change to one `DbUp.*` project has its counterpart in the other, or a stated reason

**Verification**

- [ ] Unit tests pass; integration tests pass if SQL, a provider or the runner changed
- [ ] New behaviour has a test — a new SQL-safety rule, a new split case, a new parse path
- [ ] A second run applies nothing
- [ ] `--dry-run` output looks right

**Documentation**

- [ ] `docs/` updated: new flag in [Configuration](CONFIGURATION.md), new variable in
      [Template Variables](TEMPLATE_VARIABLES.md), schema change on the table's page in `docs/PSQL/` or
      `docs/ClickHouse/` — its **Final SQL**, **Columns** and **Scripts** — and a new table also in the
      [Schema Reference](SCHEMA_REFERENCE.md) index
- [ ] The page's **Final SQL**, applied to an empty database, still creates what the migration creates
- [ ] The root `README.md` still matches reality if the CLI surface changed
- [ ] No internal hostnames, environment names or real credentials anywhere in the diff

---

## What a good migration PR looks like

A schema change is the most common contribution. It should contain, in one PR:

1. `psql/20260315_01_add_broker_id.sql` — the incremental script, in the folder of the engine that holds the
   table (`clickhouse/` for a change to the ticks table)
2. Updated `users/` grants in **both** folders, if the change adds an object the exporter must reach
3. The table's page in `docs/PSQL/` or `docs/ClickHouse/` updated: the column in **Final SQL** and **Columns**,
   the new script in **Scripts**. The Final SQL stays a single definition of the table as it now stands, never a
   list of the changes
4. A QA section describing how to see the column arrive on a real database

And in the description: why the column exists, whether the exporter has to be deployed before or after, and
whether the change is breaking for anything reading the table.

> **Warning:** The schema is a published interface for the clients running these databases. Renaming or dropping
> a column is a breaking change even when nothing inside YB reads it — say so in the release notes.

---

## Things to avoid

| Don't | Because |
|-------|---------|
| Edit an applied script | It will not re-run; the file and the deployed database drift apart silently |
| Add a rollback script | There is no rollback here by design. Roll forward with a corrective script |
| Hard-code a table name | The names are configurable in the trade server's export configuration |
| Reach for `--variables` in a committed script | Committed scripts should work with the documented variables; `--variables` is for deployment-specific values |
| Silence a warning | `TreatWarningsAsErrors` is on deliberately — fix the cause |

---

## See also

[Development](DEVELOPMENT.md) · [Writing Migrations](WRITING_MIGRATIONS.md) ·
[Architecture](ARCHITECTURE.md) · [Schema Reference](SCHEMA_REFERENCE.md)
