# Design — [PSQL] Trading Accounts snapshot table and triggers

## How it works

```
trade server --COPY--> trading_account_events --trigger--> trading_accounts
```

The trade server appends events. A row trigger on each event maintains `trading_accounts`, the latest state of
every live account. Both writes happen in the exporter's transaction, so the two tables cannot drift.

| `event_type` | Meaning | The trigger |
|---|---|---|
| `0` | account state | upserts the account's row, stamped with the event's `id` |
| `1` | deletion | deletes the account's row |
| `2` | resync started | **nothing** — the row itself is the watermark |
| `3` | resync finished | deletes rows stamped older than the newest `2` before it |
| other | unknown | nothing — stored in the log, not projected |

`trading_account_events` has 17 columns and 2 indexes. `trading_accounts` carries the same account fields,
keyed on `(sender_id, trading_account_id)`, plus `source_event_row_id` (which event wrote it) and
`time_applied`.

## Files

| File | Phase | Contents |
|---|---|---|
| `psql/20260908_01_create_trading_account_events.sql` | incremental | the event log |
| `psql/20260908_02_create_trading_accounts.sql` | incremental | the current-state table |
| `psql/views/trading_accounts_projection.sql` | `views/`, re-runnable | function, trigger, reconcile |
| `psql/views/trading_accounts_resync.sql` | `views/`, re-runnable | resync status view |
| `psql/users/exporter.sql`, `ro.sql` | `users/` | grants |

The two tables are incremental scripts, applied once. The projection is **re-runnable**, so the file is always
the current definition and `--force` re-applies it — the repair path when the trigger gets dropped or the table
edited by hand.

## Decisions

**A real table, not a view over the log.** Old events get purged, and an account can go unchanged for longer
than the retention window — so a view would silently stop returning that account. A table survives the purge
because a state row is independent of the event that produced it. Cost accepted: our code now runs inside the
trade server's write path, so a `plpgsql` error would abort the `COPY` — the export would **stall rather than
corrupt**, which is why the function is a plain column copy that never raises.

**Applied in arrival order, with no `version` check.** Row triggers fire in insertion order, which is the order
TS-2928 says to replay in. `time_inserted` cannot order events — `now()` is transaction-start time, so a whole
batch shares one. Nor can `version`: deletions and both markers carry `version 0`, so ordering by it would put
an account's deletion at the *start* of its history.

A guard comparing `version` against the stored row is ruled out for a stronger reason: **trading account ids are
reused after deletion**, so a reused id legitimately starts again at a low version. A `>=` guard would reject
every event of the new account and leave the row showing the previous holder's data indefinitely — verified. A
strict `>` guard is wrong even without reuse, because a resync re-announces an unchanged account at the version
it already had, so the guard would skip the row, leave it stamped before the resync, and the sweep would then
delete a live account. Last write wins is what makes reuse come out right, including when no deletion event
arrives at all and the reuse is first seen in a resync.

**A resync sweeps at the end marker, not the start.** Clearing rows at the start is the obvious reading of
"discard everything previously known", and it is wrong for a table. An in-process cache can discard and rebuild
because it hides its own reads until ready; this table has no such gate, and a resync spans many transactions —
so clearing at the start would empty the table for the whole resync, on **every** startup that runs one.
Instead each row is stamped, and the end marker deletes rows stamped older than the resync's start.

**No stored resync state.** The watermark is read back out of the log, so a start marker needs no handling at
all — no state table, no grants for one, no trigger write. Safe because an account can only be wrongly revived
while its last state event is still retained, that event is older than the marker that swept it, and ids rise
with time, so any purge by age that keeps the event keeps the marker. `trading_accounts_resync` is likewise a
view over those markers rather than a table, so it cannot disagree with the log.

**`SECURITY INVOKER`, with the exporter granted write on `trading_accounts`.** The wider grant gives nothing
away: anyone who can insert events can already empty the table through the trigger. `SECURITY DEFINER` would
make behaviour depend on which role ran the migration, which nothing here records.

**No dedup key.** Delivery is at-least-once and the schema has no per-event id, so a redelivered event lands as
a second log row. The state table tolerates it — a re-applied upsert is the same upsert, a re-applied delete is
a no-op — but a consumer reading the **log** can see one change twice. Fix if needed: a per-event id plus a
unique index, purely additive.

## The reconcile pass

The re-runnable script ends by applying, per account, the newest event the state row does not already reflect.
It seeds an existing deployment, closes the gap between the log table existing and the trigger existing during
a live upgrade, and is the manual repair. It cannot recover what the retention window has dropped; where it must
guess it leaves an account out rather than reviving one.

## Two bugs found by running it

- **The reconcile revived the dead.** A sweep leaves no trace in the log — an account dropped from a resync is
  gone by its *absence*, and absence is not an event — so the reconcile found the account's last pre-resync
  event and re-inserted it. Fixed by also requiring the event to be newer than the last finished resync.
- **The sweep read the wrong watermark.** `AFTER ROW` triggers fire once the whole statement has landed, so a
  batch carrying `end_A` then `start_B` had `end_A` sweeping against **B's** marker, deleting everything resync
  A had just re-announced. Fixed by bounding the lookup to markers before the end marker's own row.

Both would have looked plausible in production, which is why the outstanding integration tests matter.

## Monitoring

`version` is stored but never compared, so a gap or a regression is absorbed rather than rejected. Detect it
instead — continuity is checked per *incarnation*, since a reused id restarts the version sequence:

```sql
WITH e AS (SELECT sender_id, trading_account_id, id, event_type, version,
                  count(*) FILTER (WHERE event_type = 1) OVER (
                      PARTITION BY sender_id, trading_account_id ORDER BY id
                      ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS incarnation
           FROM trading_account_events WHERE event_type IN (0, 1))
SELECT trading_account_id, incarnation, prev_version, version, id FROM (
  SELECT sender_id, trading_account_id, incarnation, id, version,
         lag(version) OVER (PARTITION BY sender_id, trading_account_id, incarnation ORDER BY id) AS prev_version
  FROM e WHERE event_type = 0) v
WHERE prev_version IS NOT NULL AND version NOT IN (prev_version, prev_version + 1);
```

Also worth a check: `SELECT DISTINCT event_type FROM trading_account_events` against the four known values.

## For the backend team

1. **Confirm the `event_type` values** — `0` state, `1` delete, `2` resync start, `3` resync end. The trigger
   branches on these, and TS-2928's design writes them as strings, so they are not settled.
2. **`COPY` must write `''`, not `\N`, for empty strings.** `PgWriter` has only NULL-producing helpers today,
   and a `NULL` into a `NOT NULL` column aborts the whole batch.
3. **TS-2928's SPEC says non-delete events are recorded in "monotonically increasing `version` order".** That
   cannot hold once an id is reused after deletion, since the reused id restarts at a low version. Nothing here
   depends on it, but it should be corrected or scoped to one incarnation of an id, because anyone who builds
   on it as written will get a reused id wrong.
4. **`full_resync_on_startup` defaults to `false`.** Unless the trading account pipeline sets it, no resync ever
   runs — markers never arrive, the sweep never runs, and deletions missed during downtime are never
   reconciled. It fails quietly.

## Remaining work here

Documentation (`SCHEMA_REFERENCE.md` plus new `BOOLEAN` and `INTEGER` type-mapping rows,
`TEMPLATE_VARIABLES.md`, the variable checklists, `USERS_AND_PERMISSIONS.md`, `FAQ.md`, `README.md`), the
`post/run_on_every_build.sql` deployment line, and integration tests for the projection.

*One repo gotcha: DbUp's variable substitution claims `$name$`, so a custom dollar-quote tag fails the script.
Use a bare `$$`, as `psql/users/exporter.sql` already does.*
