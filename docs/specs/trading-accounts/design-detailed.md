# Design — [PSQL] Trading Accounts snapshot table and triggers

*Implemented and verified against `postgres:18` in `psql/20260908_01_create_trading_account_events.sql`,
`psql/20260908_02_create_trading_accounts.sql`, `psql/views/trading_accounts_projection.sql` and
`psql/views/trading_accounts_resync.sql`. Two defects found by running it, not by reading it, are recorded in
KD-5 (both of them) and C-27.*

## 1. Alternatives & Rationale

### KD-1: How is current `TradingAccount` state derived from the exported event log?

**Option 1: Nothing here** — every consumer reduces the log itself and keeps its own copy.

**Option 2: A plain view** — `DISTINCT ON (sender_id, trading_account_id) … ORDER BY … id DESC` over the log,
excluding accounts whose newest retained event is a deletion.

**Option 3: A materialized view**, fully recomputed on a schedule.

**Option 4 (chosen): A real table maintained by an `AFTER INSERT` trigger on the log.**

| Axis | 1 (consumer reduces) | 2 (view) | 3 (materialized view) | 4 (trigger-maintained table) |
|---|---|---|---|---|
| Still correct once aged events are purged | The consumer's problem — which is the work this ticket exists to remove | **No.** An account that has not changed inside the retention window loses its last state-carrying event and simply vanishes from the view (C-23) | **No** — same defect, plus whatever the last refresh happened to catch | **Yes.** A state row is independent of the event that produced it, and a purge is a `DELETE` while the projection fires on `INSERT` only |
| Read cost | n/a | Grows with the log: every read re-reduces the retained history of every account it touches | Cheap to read, expensive to refresh — PostgreSQL has no incremental refresh, so every refresh is a full recompute | Cheap: a ~100k-row table, read by primary key or scanned outright |
| Staleness | Consumer-defined | None | Up to one refresh interval | None — written in the same transaction as the event |
| Consistency with the log and the cursor | A separate system with its own failure modes | Exact by construction | Refresh runs outside the export transaction, so a reader can see a set the log has already moved past | **Exact.** One transaction covers the event row, the state row and `##cursor_table##` (C-3), so the three cannot drift |
| What has to be running for it to be right | A consumer | Nothing | A scheduler this repository does not have and should not acquire — no cron, no `pg_cron`, no service | Nothing beyond the export itself |
| Precedent here | The status quo | `psql/views/` is exactly this shape (C-7, C-10) | None. "Materialized views" is named in the phase description but nothing uses it | **None.** No trigger, no `plpgsql` function, no `BOOLEAN` column and no materialized view exists anywhere in this repository today (C-13) |
| Failure mode | Silent per-consumer divergence | None | A missed refresh is invisible | A `plpgsql` error aborts the export batch that hit it — this repository's code is now in the trade server's write path |

**Chosen: Option 4.** Only a real table survives the retention purge, and only a trigger keeps it exactly in
step with the log without anything having to run. The retention point decides this rather than being a
preference: TS-2928's SPEC permits events to be purged past a bounded window (C-23), and an account can sit
unchanged for far longer than any such window, so a derive-on-read answer is not merely slower — it is wrong,
and wrong in the quietest possible way: the account is simply not there. Nor does the resync mechanism rescue
it, because a resync is gated off by default and fires only on startup (C-22), so its cadence on a long-lived
process is unbounded and certainly not shorter than the retention window.

The cost taken on with it is real and should not be glossed: for the first time, code from this repository sits
inside the trade server's write path (C-13 — there is nothing of the kind here today). A `plpgsql` error raised
by the trigger aborts the exporting `COPY`, its batch and its cursor update together — so the export **stalls
rather than corrupts**, but it does stall, and the trade server's log is where that surfaces. Two things contain
that: the function is a mechanical column copy with no computation in it (KD-7), and it never raises — an event
type it does not recognise is left unprojected rather than rejected (KD-3).

*Rejected: Option 2 (view).* Right shape, wrong lifetime — a view can only answer from what the log still holds.
Kept as a follow-up in a different role: a view **over** the state table is the natural place to give reporting
friendlier semantics for the exporter's sentinel values (KD-7), and `psql/views/` already exists for that.
*Rejected: Option 3 (materialized view).* Adds a scheduler this repository has no way to own, and still cannot
outlive the retention window.
*Rejected: Option 1.* The status quo the ticket exists to replace.

**Also considered and rejected, on the log table itself: declarative partitioning by month**, which would turn
the retention purge into a `DETACH` rather than a `DELETE`. It is left out on precedent and scope rather than on
a hard blocker: `##trades_table##` is unpartitioned and is far larger (C-4), retention is the deployment's to
implement and none exists yet, and partitioning interacts with the row trigger and with any future unique index
(a partitioned table's unique index must include the partition key, which would rule out the deduplication key
in KD-9's follow-up). Worth revisiting if and when a retention policy is actually written.

---

### KD-2: The account's fields are typed columns

*Options: **typed columns**, one per field · one self-describing document column · both.*

**Chosen: typed columns**, and settled — recorded here only so the reasoning is not lost.

Two axes decided it. Typed columns make SPEC.md's no-credential-material invariant a property of the schema —
there is no `password` column, so it cannot arrive (C-25) — rather than a promise about what upstream code
chose to serialize into something the `ro` role can read (C-7). And they keep value validation at `COPY`, where
it is free and names the offending column, instead of moving it into the trigger, where a failed cast would
abort the export batch (KD-1).

The argument on the other side was schema evolution, and it is narrower than it looks: adding a column is
metadata-only and instant in modern PostgreSQL, and a field turning optional is a metadata-only
`DROP NOT NULL`. The real cost is deploy **ordering** — the exporter names its columns in the `COPY` (C-24), so
the migration has to land first — and every other exported table here already carries that property (C-4), with
the review checklist already asking which order a change deploys in (C-18).

*Available later, purely additive:* a verbatim document column alongside the typed ones. It is the only shape
that can recover a field for rows written before its column existed. Deferred because its payoff depends on a
convention the backend team would have to adopt and keep, which this repository cannot enforce.

---

### KD-3: Trigger granularity, and what establishes the order events are applied in

**Option 1 (chosen): `AFTER INSERT … FOR EACH ROW`.** One `plpgsql` invocation per event row, branching on
`event_type`. The order of application is the order rows were inserted, which is the log's recorded order.

**Option 2: `AFTER INSERT … FOR EACH STATEMENT REFERENCING NEW TABLE`.** One invocation per `COPY`, working
set-based over the transition table.

**Option 3: `BEFORE INSERT … FOR EACH ROW`.** As Option 1, but fired as each row arrives rather than queued to
the end of the statement.

| Axis | 1 (after, per row) | 2 (after, per statement) | 3 (before, per row) |
|---|---|---|---|
| Ordering | Implicit and correct — rows fire in insertion order, which is the key TS-2928 makes reconstruction depend on (C-23) | Explicit: needs `ORDER BY id`, and one batch may mix state, deletion and both markers for the same account, so a single set-based statement cannot express "last one wins" without either a `plpgsql` loop over the transition table (Option 1's cost with extra indirection) or several carefully sequenced passes | Same as 1 |
| Ordering key available | `id`, the log's identity column (C-1). **Not** `time_inserted`: `now()` is transaction-start time, so every row of one batch shares it (C-26) and it cannot break a tie | Same key, but it has to be written out and kept right | Same as 1 |
| Cost of a resync batch | One `plpgsql` call per row: ~100k calls spread over the resync's batches (C-23), tens of milliseconds per batch of a few thousand | One call per batch, set-based — materially cheaper at this size, but the size does not warrant it | Same as 1, without the after-trigger queue |
| After-trigger queue | 100k queued entries for a large `COPY`; PostgreSQL spills it to disk if needed. Bounded by the exporter's batch size, not by the resync size | None | None |
| Semantics | Correct: "the event is recorded, therefore project it" | Correct | Wrong tool. A `BEFORE` trigger's contract is to inspect or alter the row being written, and it can suppress it. The projection has no business being able to do either |
| Verification burden | Low | Higher — transition-table behaviour under `COPY` and the per-account last-wins logic both need proving, with nothing in this repository to pattern-match against (C-13) | Low |

**Chosen: Option 1.** At this volume the per-row cost is not worth trading correctness-by-construction for. The
ordering the projection needs is exactly the ordering PostgreSQL gives an `AFTER … FOR EACH ROW` trigger for
free, so nothing has to assert it, and the one genuinely awkward case — a batch mixing several event kinds for
one account — needs no thought at all. Option 2 stays documented as the upgrade path if account churn ever
changes shape enough to matter.

**No `version` guard**, and `version` cannot order events either. Deletions and both resync markers carry
`version 0` (C-20), so ordering an account's history by `version` would place its deletion at the *start*. That
is TS-2928's own conclusion, and why its SPEC keys reconstruction off recorded order (C-23).

A guard comparing an arriving `version` against the stored row is ruled out by **id reuse: a trading account id
may be reused by a different account after deletion (C-30)**, so a reused id legitimately restarts at a low
version. A `>=` guard would then reject every event of the new account and leave the row showing the previous
holder's data indefinitely, with nothing to heal it — verified. A strict `>` guard is wrong independently of
reuse: a resync re-announces an unchanged account at the `version` it already had, so it would skip the row,
leave it un-restamped, and KD-4's sweep would then delete a live account.

Last-write-wins is therefore not merely tolerable but necessary. The upsert replaces every column, so a reused
id resolves correctly whether its deletion arrives first or the reuse is first seen in a resync re-announcement
with no deletion event at all. The distinction between the two guard forms matters: an earlier revision of this
document called any `version` guard "actively wrong", which was true only of the strict form.

**Redelivery is absorbed by the projection, not blocked before it.** The agreed schema carries no per-event id,
so there is no deduplication index and a redelivered event lands as a second log row — a deliberate divergence
from how `##trades_table##` handles the same problem (C-4). It is safe for the derived table because every
branch is idempotent under arrival order: a re-applied upsert writes the same values, a re-applied deletion is a
no-op, and a resent batch carries its events in their original order because the exporter resumes from the
committed cursor position (C-3). See KD-9 for what it does cost.

**An unrecognised `event_type` is not an error.** The alternative — `RAISE EXCEPTION` — would turn any future
event type the trade server adds into a hard export stall on every deployment that had not migrated yet. The
log keeps the row either way, so nothing is lost, and detection belongs in monitoring
(`SELECT DISTINCT event_type` against the known set) rather than in the write path.

---

### KD-4: What a resync does to the state table

TS-2928 sends a resync as `snapshot_start`, an unbounded stream of ordinary state-carrying events, then
`snapshot_end`, with no pass id and no row count, correlated only by position in the per-sender stream (C-21).
It explicitly may span several transactions (C-23), so the projection cannot treat one as atomic.

**Option 1: Purge on `snapshot_start`** — delete every row for that sender, then let the re-announcements refill
it.

**Option 2 (chosen): Mark and sweep** — `snapshot_start` records the log row it arrived as, as that sender's
resync watermark; every projected row carries the log row that wrote it; `snapshot_end` deletes that sender's
rows whose stamp is older than the watermark.

**Option 3: Shadow table and swap** — project the resync into a second table and rename at `snapshot_end`.

| Axis | 1 (purge on start) | 2 (mark and sweep) | 3 (shadow and swap) |
|---|---|---|---|
| The table during a resync | **Empty, then partially filled.** With `full_resync_on_startup` on that is every trade server restart (C-22) — a reporting consumer reads zero live accounts for the duration | Complete throughout: re-announced accounts are current, accounts not yet reached carry their previous state | Complete throughout — the live table is untouched until the swap |
| What is stale during a resync | Nothing is stale, because nothing is there | Accounts the resync has not reached yet, and accounts deleted while the sender was down. Bounded, plausible, and flagged by the in-progress marker | Everything, by exactly one resync |
| Interrupted resync | **Worst case.** A sender that dies after `snapshot_start` leaves a permanently truncated table until the next completed resync | Stale extra rows linger until a later resync completes — the same window, with data in it instead of a hole | The shadow table is abandoned and the live table never moved. Cleanest failure |
| Atomicity of the changeover | n/a | Per transaction, like everything else | Needs DDL (`ALTER TABLE … RENAME`) from a trigger, taking `ACCESS EXCLUSIVE` on both tables mid-export — the trigger would be issuing DDL inside the trade server's write path |
| Cost at `snapshot_start` | One `DELETE` of the sender's rows | One tiny upsert into a state table | Create or truncate a shadow table |
| Extra schema surface | None | One `BIGINT` stamp column on the state row, and nothing else — the watermark is read back out of the log (below) | A second full copy of the state table, plus whatever holds the swap bookkeeping |
| Write amplification | A 100k-row delete then a 100k-row insert per resync | A 100k-row upsert per resync. The stamp column is what avoids the alternative of flagging every row stale at `snapshot_start`, which would rewrite the whole table twice per resync and hand autovacuum the bill | Two full copies live at once |
| Privileges the writer needs | `DELETE` | `DELETE` — for real deletions and for the sweep | `DELETE` plus DDL rights (KD-6) |

**Chosen: Option 2.** Option 1's hole is not a corner case, it is the normal path: the resync trigger TS-2928
chose is process startup (C-22), so every restart of the trade server would blank the reporting picture for as
long as the resync takes. Bounded staleness beats total unavailability, and the in-progress marker makes that
staleness legible instead of silent. Option 3 has the best failure mode of the three and is rejected on the
means rather than the end: it requires a trigger to run DDL inside the export transaction, which is a far larger
thing to accept than a stamp column.

**Where the watermark lives.** Three places were possible: derived from the log (`max(id)` of that sender's
`snapshot_start` rows), a boolean on every state row, or a small per-sender state table. A per-row boolean is
out immediately — it means rewriting every row of the table at `snapshot_start`, twice the write amplification
of the upserts themselves, and handing autovacuum the bill.

**Chosen: derived from the log.** The state table's own retention was the argument against a state table, and
the mirror-image argument was initially made against deriving — that the retention purge is actively deleting
from the table the watermark would be read from. **That argument is wrong, and the decision turns on seeing
why.** Resurrection is only possible if an account's last state event is still retained; that event is *older*
than the `snapshot_start` that swept the account; and ids rise with time — so any purge by age that still holds
the event necessarily still holds the marker. The watermark is guaranteed available in exactly the cases where
it is needed. A partial index on the marker rows (`WHERE event_type IN (2, 3)`) makes the lookup a couple of
rows, and it happens once per resync.

Deriving is then better than a state table on every remaining axis, not merely cheaper. It removes a table, a
template variable, a set of grants and a write from the trigger. `snapshot_start` becomes a **complete no-op** —
the row's existence in the log *is* the record, so the marker's only job is to have an id. And it is strictly
more robust in the one case they differ: if the trigger were absent when `snapshot_start` arrived but present
at `snapshot_end`, a state table would hold a stale watermark or none, while the log holds the marker either
way.

The consumer-facing *is a resync in progress* signal, which SPEC.md requires to be readable, then costs no state
at all: it is an aggregate over the same marker rows, shipped as a view in `psql/views/`. Its timestamps come
from the markers' own `time_inserted` rather than from whenever a trigger got round to writing a row, which is
also the more truthful answer. Being a view, it is deletable later at zero cost if nothing ends up reading it.

**`snapshot_end` with no `snapshot_start`** needs no special case: with no state row for that sender the
watermark comparison is against `NULL` and the sweep deletes nothing. **A second `snapshot_start`** simply
advances the watermark, which is the correct semantics — the later resync supersedes the earlier one.

---

### KD-5: Which migration phase carries the function and the trigger, and how the projection is repaired

**Option 1: Everything in one incremental script** in `psql/` — tables, indexes, function, trigger.

**Option 2 (chosen): Tables and indexes in an incremental script; function, trigger and a
reconcile-from-the-log statement together in one re-runnable `psql/views/` script.**

**Option 3: A new phase folder** (`psql/functions/` or `psql/triggers/`), re-runnable, ordered after the root.

**Option 4: `psql/post/`** — re-runnable, ordered last.

| Axis | 1 (incremental) | 2 (re-runnable in `views/`) | 3 (new phase) | 4 (`post/`) |
|---|---|---|---|---|
| Can the current projection logic be read in one file? | **No.** A logic change ships as another incremental script, so the live definition is whatever the newest of N scripts said — C-10 forbids editing an applied one | Yes — the file *is* the definition, re-applied whenever its content changes (C-10) | Yes | Yes |
| Fresh-install ordering | All in one transaction, so the trigger exists before anything else runs | Root (order 2) creates the tables, `views/` (order 3) the trigger — see the gap below | Same as 2 | Same as 2, and after `users/`, so grants would be applied before the objects they cover are wired up |
| The gap between the table existing and the trigger existing | None | Real: on a **live** upgrade the trade server can export into the new log table inside that window, and those events would never be projected. Closed by the reconcile statement in the same script | Same as 2, same fix | Same as 2, same fix |
| Cost of changing the projection later | A new incremental script per change | Edit the file; the MD5 change re-applies it (C-10) | Same as 2 | Same as 2 |
| Repair when the *effect* is lost but the content is not — the trigger is dropped, or an earlier version of the projection wrote rows wrongly | Nothing to re-run. A corrective incremental script per incident | **`--force`**, which this repository already documents for exactly this case, "someone dropped a view by hand" (C-10) | Same as 2 | Same as 2 |
| Code change needed | None | None | **Yes** — the five phases are a hard-coded list (C-8), so a sixth touches `DbUpRunner`, `ScriptSubFolders` and every place the docs enumerate the phases | None |
| Naming honesty | n/a | A trigger-maintained table is an incrementally-maintained materialized view, and `psql/views/` is documented as "views and materialized views" — a stretch, but a defensible one | Exact | Poor: `post/` is for what runs after the schema, such as TTLs |

**Chosen: Option 2.** It is the only option that gives all three of: one file that always reads as the current
definition, no C# change, and `--force` as the documented repair path. The reconcile statement is what makes it
safe, and it earns its place three times over — it closes the fresh-install ordering gap, it seeds an existing
deployment whose log already holds history, and it is the manual repair tool. It is also free on a green-field
install, where the log is empty and it inserts nothing.

The reconcile is exact rather than best-effort *with respect to what the log holds*, because of the lock:
creating the trigger takes a lock on the log table that conflicts with the exporter's `INSERT`, and the migrator
runs each PostgreSQL script inside one transaction (C-9), so the exporter is held off for the rest of the script
and the reconcile sees a stable table. It applies, per `(sender_id, trading_account_id)`, the newest retained
event whose log row is newer than the stamp the state row already carries: a state-carrying event upserts, a
deletion deletes, markers are skipped.

**One defect in the reconcile, found by running it rather than reviewing it, and the guard it needs.** A first
cut of the reconcile resurrected the dead: an account that a completed resync had swept came straight back on
the next `--force`. The cause is that **a sweep leaves no trace in the log** — an account dropped from a resync
is gone by virtue of its *absence* from the re-announcements, and absence is not an event — so the reconcile
found the account's last pre-resync state event, saw no derived row, and re-inserted it. The fix is a second
condition: the event must also be newer than the sender's most recent *completed* resync watermark. That
watermark is always available when it is needed, which is why it needs no stored state: resurrection requires
the account's last state event to still be retained, that event is older than the marker that swept it, and ids
rise with time, so any purge by age that holds the event holds the marker (KD-4). This is the class of defect
that would have sat in production looking entirely plausible — a deleted account reading as live after any
migration.

**A second defect, in the sweep, from the same source: running it.** The end marker read its watermark as
`max(id)` of the sender's start markers, unbounded. An `AFTER ROW` trigger fires once the whole statement has
landed (C-26), so by then every row of the batch is already in the table — including a *later* resync's start
marker, if the batch carried one. A batch holding `end_A` followed by `start_B` therefore had `end_A` sweeping
against B's watermark and deleting everything resync A had just re-announced. Fixed by bounding the lookup to
markers older than the end marker's own row, which makes it "the newest start marker before this end marker"
whatever else the batch contains. It needed two resyncs' markers in one batch to bite — unlikely with
100k-account resyncs and ordinary batch sizes — but correctness that depends on batch sizing does not belong in
a trigger.

Two honest limits on the reconcile, both carried into SPEC.md rather than left implicit. It can only reconstruct
what the retention window still holds — an account whose last event has aged out cannot be recovered locally, and an
account deleted before the projection existed whose deletion has aged out stays visible. And it deliberately
does **not** rebuild resync marker bookkeeping: a resync interrupted by a migration is finished by the next
resync, not by the reconcile. In both cases the repair is a resync from the trade server (C-22), which is where
the authority over "which accounts are live" actually sits.

The trigger is created with `DROP TRIGGER IF EXISTS` followed by `CREATE TRIGGER`, not
`CREATE OR REPLACE TRIGGER`. Both take a lock that blocks the exporter and both are atomic inside the script's
transaction, so the choice is purely about version reach: `CREATE OR REPLACE TRIGGER` needs PostgreSQL 14
(C-26), and while the test containers run 18, no minimum version is stated anywhere in this repository (C-19),
so there is nothing to justify raising one for syntax sugar.

---

### KD-6: Under whose privileges does the projection run?

**Option 1 (chosen): `SECURITY INVOKER`** — the default. The function runs as the exporter role, which therefore
needs `INSERT`, `UPDATE` and `DELETE` on the two projection tables.

**Option 2: `SECURITY DEFINER`** — the function runs as its owner. The exporter keeps exactly the grants it has
today and needs nothing on the projection tables.

| Axis | 1 (`SECURITY INVOKER`) | 2 (`SECURITY DEFINER`) |
|---|---|---|
| Grants the exporter needs | Widened: `INSERT, UPDATE, DELETE` on both projection tables, on top of `INSERT, SELECT` on the log | Unchanged — `INSERT, SELECT` on the log and nothing more |
| Consistency with the stated posture | `psql/users/exporter.sql` says an exported record "is never updated or deleted" (C-6). Still true of *exported records* — the projection tables are derived state, not exported records — but the comment has to be amended so the file does not read as false | Preserved verbatim |
| Marginal risk of the wider grant | **Effectively nil.** A principal that can insert events can already empty the projection through the trigger by writing a `snapshot_start`, so `DELETE` grants no capability it did not already have | n/a |
| New attack surface | None beyond the grant itself | A `SECURITY DEFINER` function is a privilege-escalation surface and must pin `search_path` to be safe at all |
| Behaviour depends on who migrated | No | **Yes** — the effective owner is whichever role ran the migration, so a deployment that migrates as a different role silently gets different behaviour. Nothing in this repository records or checks that role |
| Reviewability | Explicit and greppable: the grants sit in `users/exporter.sql` and `users/ro.sql` next to every other grant (C-6, C-7) | Implicit: the authority is a property of the function's owner, visible only in `pg_proc` |
| Precedent | The `users/` phase is the established place for privileges | None here (C-13) |

**Chosen: Option 1.** The wider grant buys nothing an attacker did not already hold, whereas
`SECURITY DEFINER`'s dependence on the migrating role is a real, silent, deployment-specific behaviour
difference, and its `search_path` requirement is a footgun this repository would be introducing with no
precedent to lean on. The `ro` role gains `SELECT` on all three new tables through the same REVOKE-then-GRANT
convergence the file already uses (C-7).

---

### KD-7: Does the projection reinterpret what the exporter writes?

TS-2928 chose sentinel defaults over `NULL` throughout: every column is `NOT NULL`, and a field that does not
apply carries a type-appropriate zero (C-20). So `introducer = 0` means "no introducing broker", `comment = ''`
means "no comment", and an epoch `time_password_last_changed` means "never changed".

**Option 1 (chosen): Pass through.** The state row carries the same columns with the same sentinel meanings.

**Option 2: Translate on the way in** — sentinels become `NULL` in the state table.

| Axis | 1 (pass through) | 2 (translate) |
|---|---|---|
| Trigger complexity | A mechanical column copy. Nothing to get wrong, nothing to review beyond the column list | A per-column rule, and a second place where the meaning of a field lives |
| Consistency with the log | Exact: a state row and its event say the same thing | A state row and its event disagree by design, so support has to know the translation to compare them |
| Consistency with the upstream decision | Preserved | Re-introduces one hop downstream exactly the `NULL` that TS-2928 weighed and rejected (C-20) |
| Precedent here | Every export column in this repository is `NOT NULL` with a `DEFAULT` unless the exporter genuinely writes `NULL` (C-5) | The nullable snapshot columns (C-5) are the precedent, but they are nullable because the *exporter* writes `NULL`, not because this repository translated anything |
| Convenience for reporting | `introducer = 0` rather than `introducer IS NULL` — mildly worse | Better |
| Cost of changing course later | None: a view over the state table adds the friendly form at zero cost, and `psql/views/` is where it belongs (KD-1) | A migration, plus every consumer already reading it |

**Chosen: Option 1.** The projection's whole value is that it is a faithful reduction of the log; interpretation
is a second place for semantics to live and drift. The friendly form is a view away, and deferring it costs
nothing.

**Three items on the wire contract have to be settled with TS-2928 before implementation, not assumed:**

1. **`event_type` representation — agreed with TS-2928.** `SMALLINT`, matching every other enum in this schema
   (`trade_type`, `side`, `reason`, `order_type` — all `SMALLINT` over a C++ `enum class : uint8_t`), with the
   mnemonics in a column comment and in `docs/SCHEMA_REFERENCE.md`. TS-2928's design writes the values as
   strings (`'snapshot_start'`, C-21), so either it means a mnemonic on the wire or it is prose shorthand for an
   integer. The numeric values themselves — proposed `0` state, `1` delete, `2` `snapshot_start`,
   `3` `snapshot_end` — must match the exporter's enum exactly, and this repository cannot pick them
   unilaterally.
2. **String column widths.** Taken from the trading account API schema: `external_account_id` 1–63 characters
   and `comment` 1–255 (C-25). The C++ side stores fixed-width `char` buffers (C-20), so the effective widths
   may be narrower — the exporter's descriptor is authoritative and a value longer than the column fails the
   whole `COPY`.
3. **`password` is excluded, and must stay excluded.** It is never present in API responses (C-25) and must not
   be in the export descriptor. SPEC.md makes that an invariant so that adding it later is a visible break
   rather than a quiet one.
4. **TS-2928's SPEC invariant "non-delete events are recorded in monotonically increasing `version` order"
   cannot hold under id reuse (C-23, C-30).** A reused id restarts its version sequence, so the property is
   true only within one incarnation of an id. Nothing in this design depends on it, but it should be corrected
   or scoped explicitly, because a consumer that trusts it as written will mishandle a reused id.

---

### KD-8: Names, and which objects get a `##variable##`

Three tables come out of this: the log the trade server writes, the current-state table, and the per-sender
resync state table. This repository templates an object name when a deployment might have to move it aside
(C-11), and validates every templated identifier (C-12).

| Object | Proposed variable | Proposed default | Templated because |
|---|---|---|---|
| The event log | `##trading_account_events_table##` | `trading_account_events` | The trade server names it in `export_config.json`, exactly like the five existing table variables |
| Current state | `##trading_accounts_table##` | `trading_accounts` | The trade server has no say in it, but a deployment may already own an object under that name — the same reasoning `viewVariables` is kept apart for (C-11) |
| Resync status | `##trading_accounts_resync_view##` | `trading_accounts_resync` | A view, not a table (KD-4), so it joins `viewVariables` alongside the two views over `trades` |

The log variable joins `tableVariables`; the other two need a third list of their own rather than being pushed
into `viewVariables`, which is explicitly about views (C-11). Whichever list they land in, they must also reach
`identifierVariables`, or their names go into DDL unvalidated — the comment on that array warns about exactly
this omission (C-11).

**On the word "snapshot".** The ticket calls the current-state table a snapshot table, and this design does not
name it that. Three different things would then be called snapshots: the end-of-day
`daily_statements_order_snapshots` / `daily_statements_position_snapshots` tables, TS-2928's
`snapshot_start` / `snapshot_end` resync markers, and this. `trading_accounts` says what the table holds — the
trading accounts — and leaves "snapshot" to mean the two things it already means here. If the ticket owner
prefers the ticket's wording, `trading_accounts_snapshot` is the alternative and nothing else in the design
changes.

**Function and trigger names carry the table name**, as index names already do, and for the same two reasons
(C-2): PostgreSQL truncates an identifier at 63 bytes, so what the object does comes before the table name; and
a function lives in the schema rather than on a table, so two sets of tables in one database under different
names would otherwise fight over one function. Proposed: `project_##trading_accounts_table##()` and
`trg_project_##trading_accounts_table##` on the log table.

**No foreign key** from a state row's stamp to the log's `id`, deliberately. This schema has no foreign keys at
all, and one here would make the retention purge either fail or cascade into the state table — destroying the
one property KD-1 was chosen for.

---

### KD-9: No deduplication key on the log

The agreed schema carries no per-event id, so there is no unique index and at-least-once delivery is absorbed
rather than blocked. This diverges from every other exported table here, each of which turns a redelivery into a
rejected insert via a unique key (C-4).

| Axis | No dedup key (agreed) | A per-event id plus `UNIQUE (sender_id, event_id)` |
|---|---|---|
| Effect on the derived table | **None.** Every branch is idempotent under arrival order — verified: inserting the same event twice leaves two log rows and one correct state row | None; the duplicate never reaches the trigger |
| Effect on the log | A redelivered event appears twice, so TS-2928's "one event per logical change" does not hold for a consumer reading the log directly | Holds |
| Effect on a resent batch | Safe: the exporter resumes from the committed cursor position (C-3), so a resend replays the batch in its original order, markers included | Safe |
| Failure mode on redelivery | Silent duplication in the log | The whole `COPY` aborts, as a duplicate `trade_id` does on `##trades_table##` today |
| Cost | None | One `BIGINT` per row and an index |

**Chosen: no dedup key**, by direction. The projection is unaffected, which is the part this ticket owns. What
is given up is a guarantee for anyone reading the **log** directly, and that should be stated to them rather
than assumed away.

*Available later, purely additive:* a per-event id from the exporter plus the unique index. Nothing in the
projection would change.

---

## 2. Decisions

**KD-1 — Current state is a real table, maintained by an `AFTER INSERT … FOR EACH ROW` trigger on the event
log.**
*Why:* it is the only option that outlives the log's retention window, and TS-2928 permits purging past a
bounded window (C-23) while an account may sit unchanged for far longer. A view or a materialized view over the
log would silently drop such an account, and the resync mechanism does not bound that risk because it is gated
off by default and fires only on startup (C-22). The trigger also gets exact consistency for free: the event
row, the state row and `##cursor_table##` all commit in one transaction (C-3), so the three cannot drift, and no
scheduler has to exist.
*Accepted cost:* this repository's code now runs inside the trade server's write path for the first time
(C-13). An error in the function aborts the export batch — a stall, not corruption — which is why the function
computes nothing and never raises.
*Rejected:* a view (right shape, wrong lifetime; kept as a follow-up **over** the state table, not over the
log); a materialized view (a scheduler this repository cannot own, and still bounded by retention);
consumer-side projection (the status quo).
*Also rejected:* partitioning the log by month — left out on precedent and scope (`##trades_table##` is
unpartitioned and far larger, and no retention policy exists yet to make `DETACH` worth the interaction with the
row trigger), not on a hard blocker.

**KD-2 — The log carries the account's fields as typed columns, one per field, not as a JSON payload.**
*Why:* it makes SPEC.md's no-credential-material invariant a property of the schema — there is no `password`
column, so it cannot arrive (C-25) — rather than a promise about what upstream code chose to serialize into a
blob that the `ro` role can read (C-7). It also keeps value validation at `COPY`, where it is free and names the
offending column, instead of moving it into the trigger, where a failed cast aborts the export batch — the one
failure mode KD-1 accepts and then works to contain. The evolution case for a blob is real but narrower than it
appears: adding a column is metadata-only and instant in modern PostgreSQL, and a field turning optional is a
metadata-only `DROP NOT NULL`, so the actual cost is deploy **ordering** — which every other exported table
here already carries (C-4) and which the review checklist already accounts for (C-18).
*Rejected:* a `payload JSONB` column as the only representation — defers rather than removes a migration, and
pays for it with an unenforceable security invariant, runtime casts in the write path, and an untyped
undocumented interface for the clients reading this database (C-18).
*Deferred, not rejected on the merits:* typed columns **plus** a verbatim `payload` column. It genuinely solves
deploy ordering, and it is the only option that can recover a field for rows written before its column existed
(`UPDATE … SET x = (payload->>'x')::… WHERE payload ? 'x'`). It is purely additive, so it can arrive the day
the coupling hurts; its payoff depends on the backend team adopting and keeping a payload-first convention for
new fields, which this repository cannot enforce.
*Note:* the projection reads state off an event in exactly one place, so changing course later edits one block
of one re-runnable file.

**KD-3 — `AFTER INSERT … FOR EACH ROW`, ordered by the log's identity column, with no `version` guard, and no
exception on an unrecognised `event_type`.**
*Why:* a row-level `AFTER` trigger fires in insertion order, which is exactly the recorded order TS-2928 makes
reconstruction depend on (C-23) — so the ordering needs no assertion and a batch mixing event kinds for one
account needs no special handling. `time_inserted` cannot serve as the key because `now()` is transaction-start
time and every row of a batch shares it (C-26). The `version` guard is not merely unnecessary but actively
wrong: a resync re-announces an unchanged account at the same `version`, so a guarded upsert would leave the row
un-restamped and KD-4's sweep would delete a live account, and a `>=` guard is ruled out by id reuse (C-30),
which restarts a version sequence and would leave a reused id frozen on its previous holder's data. Redelivery
needs no guard of its own: every branch is idempotent under arrival order, so a re-delivered event leaves the
same state (KD-9). An unrecognised
`event_type` is left unprojected because raising would turn any future event type into a hard export stall on
every un-migrated deployment, and the log retains the row regardless.
*Rejected:* a statement-level trigger with a transition table — materially cheaper per resync batch, but it has
to re-establish ordering explicitly and prove per-account last-wins across mixed event kinds, with no local
precedent to pattern-match (C-13). Kept as the documented upgrade path if account churn ever changes shape.
*Rejected:* a `BEFORE` trigger — the right cost profile, the wrong contract: a `BEFORE` trigger may alter or
suppress the row being written, and the projection must be able to do neither.

**KD-4 — A resync is mark-and-sweep: `snapshot_start` records a per-sender watermark, every projected row is
stamped with the log row that wrote it, and `snapshot_end` deletes that sender's rows stamped older than the
watermark. The watermark is read back out of the log, not stored.**
*Why:* purging on `snapshot_start` would empty the reporting picture on **every trade server restart** once
`full_resync_on_startup` is on (C-22), and leave it permanently truncated if the sender died mid-resync. Mark
and sweep keeps the table complete throughout, at the cost of bounded, plausible staleness — accounts the
resync has not reached yet, and accounts deleted while the sender was down — which the in-progress marker makes
legible. The watermark needs no state of its own: an account can only be resurrected while its last state
event is retained, that event is older than the marker that swept it, and ids rise with time — so any purge by
age that keeps the event keeps the marker. Deriving it removes a table, its grants and a trigger write, makes
`snapshot_start` a complete no-op, and is more robust than a stored watermark in the one case the two differ (a
trigger absent at the start marker but present at the end). The consumer-facing "is this settled?" signal is
then an aggregate over the same markers, shipped as a view.
*Rejected:* purge on `snapshot_start` — a hole on the normal path, not a corner case.
*Rejected:* shadow table and swap — the best failure mode of the three, rejected on the means: it needs a
trigger to issue DDL inside the trade server's export transaction.

**KD-5 — Tables and indexes ship in an incremental script; the function, the trigger and a
reconcile-from-the-log statement ship together in one re-runnable `psql/views/` script.**
*Why:* only a re-runnable script keeps one file readable as the current definition and makes `--force` the
repair path this repository already documents for a lost effect (C-10); a new phase folder would do the same but
needs a C# change to a hard-coded five-phase list plus every doc that enumerates it (C-8). The reconcile
statement pays for itself three times: it closes the window between the log table existing (root phase) and the
trigger existing (`views/` phase) during a live upgrade, it seeds a deployment whose log already holds history,
and it is the manual repair. It is exact with respect to the retained log because creating the trigger locks the
log against the exporter for the rest of the script's single transaction (C-9).
*Stated limits, carried into SPEC.md:* the reconcile cannot recover what the retention window has dropped, and
it does not rebuild resync marker bookkeeping. Both repair by a resync from the trade server (C-22), which is
where authority over the live set sits.
*Rejected:* one incremental script — no way to re-run it, and the live definition ends up spread across however
many corrective scripts followed it.
*Rejected:* `post/` — runs after `users/`, so grants would land before the objects they cover are wired up, and
the phase means something else (C-8).

**KD-6 — The trigger function is `SECURITY INVOKER`; `users/exporter.sql` grants the exporter
`INSERT, UPDATE, DELETE` on the two projection tables and `users/ro.sql` grants `SELECT` on all three new
tables.**
*Why:* the wider grant gives away nothing — a principal that can insert events can already empty the projection
by writing a `snapshot_start` — while `SECURITY DEFINER` makes behaviour depend on which role happened to run
the migration, something nothing here records or checks, and requires a pinned `search_path` to be safe at all,
with no local precedent (C-13). Grants are explicit, greppable and reviewed in the file where every other grant
lives (C-6, C-7).
*Consequence:* the comment in `psql/users/exporter.sql` stating that an exported record is never updated or
deleted (C-6) needs amending. It stays true of exported records; the projection tables are derived state, and
the file should say so rather than read as false.
*Rejected:* `SECURITY DEFINER` — preserves the exporter's grants exactly, at the price of deployment-dependent
authority and a footgun.

**KD-7 — The projection copies columns; it does not reinterpret them. The exporter's sentinel values survive
into the state table unchanged.**
*Why:* a faithful reduction is the whole value of the projection, and translating sentinels to `NULL` would put
the meaning of a field in a second place and re-introduce one hop downstream exactly the `NULL` TS-2928 weighed
and rejected (C-20). It also keeps the function a mechanical column copy, which is what makes it reviewable.
Reporting convenience is a view away, over the state table, at zero cost and with no migration (KD-1).
*Rejected:* translating sentinels to `NULL` on the way in — friendlier to read, at the price of the state table
and its own log disagreeing by design.
*Settled with TS-2928:* the `event_type` representation and its numeric values — `SMALLINT`, with `0` state,
`1` delete, `2` resync start, `3` resync end. *Open, and not this repository's to close:* the true
widths of `external_account_id` and `comment`, and the confirmation that `password` is absent from the export
descriptor. All three are wire-contract items to settle with TS-2928 before implementation.

**KD-8 — `##trading_account_events_table##` (`trading_account_events`) and `##trading_accounts_table##`
(`trading_accounts`), plus `##trading_accounts_resync_view##` (`trading_accounts_resync`) as a view; the
function and trigger names carry the state table's name; no foreign key from the stamp to the log.**
*Why:* the log is named by the trade server's export configuration, so it belongs with `tableVariables`. The
state table is named by nobody upstream but is still templated, for the reason `viewVariables` already exists —
a deployment may own an object under that name (C-11) — and needs a third list of its own, since it is neither
written by the trade server nor a view. The resync view joins `viewVariables` directly. All of them must also
reach `identifierVariables` or their names go into DDL unvalidated (C-11, C-12). Function and trigger names carry the table name for the two
reasons index names already do: 63-byte truncation, and a schema-level function being global where a table is
not (C-2). The foreign key is omitted because it would make the retention purge either fail or cascade into the
state table, destroying the property KD-1 was chosen for — and this schema has no foreign keys anywhere.
*Named deliberately, not by default:* the current-state table is `trading_accounts`, not
`trading_accounts_snapshot`, so that "snapshot" keeps meaning the two things it already means here — the
end-of-day order and position snapshot tables, and TS-2928's resync markers. `trading_accounts_snapshot` is a
drop-in alternative if the ticket owner prefers the ticket's wording.

**KD-9 — The log carries no deduplication key; at-least-once delivery is absorbed by the projection instead.**
*Why:* by direction, and it costs the derived table nothing — every branch is idempotent under arrival order, so
a redelivered event leaves the same state, and a resent batch replays in its original order because the exporter
resumes from the committed cursor position (C-3). Verified directly: the same event inserted twice leaves two
log rows and one correct state row.
*Consequence, to be stated to consumers rather than assumed away:* a reader of the **log** can see one logical
change twice, so TS-2928's exactly-once-per-change property does not survive into PostgreSQL. Every other
exported table here does preserve it, via a unique key (C-4), so this is a divergence worth naming.
*Available later, purely additive:* a per-event id plus `UNIQUE (sender_id, event_id)`. The projection would not
change.

### The shape these decisions produce

`##trading_account_events_table##` — written only by the trade server, 17 columns, `NOT NULL` throughout. The
exporter writes every column on every row, so a field that does not apply carries a type-appropriate zero rather
than relying on a column default (C-20):

| Column | Type | Notes |
|---|---|---|
| `id` | `BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY` | Recorded order. The projection's ordering key (KD-3) |
| `time_inserted` | `TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()` | Transaction start, so shared across a batch — **not** an ordering key (C-1, C-26) |
| `sender_id` | `BIGINT NOT NULL` | The emitting trade server instance |
| `event_type` | `SMALLINT NOT NULL` | `0` state, `1` delete, `2` resync start, `3` resync end — to be confirmed (KD-7) |
| `trading_account_id` | `BIGINT NOT NULL` | `0` on both markers (C-21) |
| `version` | `BIGINT NOT NULL` | `0` on a deletion and on both markers (C-20) |
| `group_id`, `client_id`, `introducer` | `BIGINT NOT NULL` | `introducer = 0` means none |
| `time_created`, `time_modified`, `time_password_last_changed` | `TIMESTAMP WITH TIME ZONE NOT NULL` | Epoch means "never" for the last of the three |
| `leverage` | `INTEGER NOT NULL` | `int32` upstream (C-25) |
| `enabled`, `allow_trading` | `BOOLEAN NOT NULL` | |
| `external_account_id` | `VARCHAR(64) NOT NULL DEFAULT ''` | Width to be confirmed (KD-7) |
| `comment` | `VARCHAR(256) NOT NULL DEFAULT ''` | Width to be confirmed (KD-7) |

No `password` column, and none is to be added — that absence is what makes SPEC.md's no-credentials invariant
enforceable rather than a promise (C-25).

Two indexes: `(sender_id, trading_account_id, id)` for the reconcile and for per-account history, and a partial
`(sender_id, event_type, id) WHERE event_type IN (2, 3)` for the resync watermark lookup — a marker pair is two
rows per resync, so the partial index stays tiny where an unfiltered one would have to be scanned to find them.
Nothing further, following this repository's stated index restraint (C-16) — the natural next candidates are
`client_id` and `group_id`, deliberately left until a real workload asks.

`##trading_accounts_table##` — written only by the trigger. The same content columns and the same sentinel
meanings (KD-7), plus:

| Column | Type | Notes |
|---|---|---|
| `sender_id`, `trading_account_id` | `BIGINT NOT NULL` | `PRIMARY KEY (sender_id, trading_account_id)`. No identity column, following `##cursor_table##` (C-3) |
| `version` | `BIGINT NOT NULL` | As of the event that wrote the row |
| `source_event_row_id` | `BIGINT NOT NULL` | The log `id` that produced this row. KD-4's stamp, and the traceability SPEC.md requires. No foreign key (KD-8) |
| `time_applied` | `TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()` | When the projection last wrote the row |

`##trading_accounts_resync_view##` — no table behind it, an aggregate over the sender's marker rows (KD-4):

| Column | Derivation | Notes |
|---|---|---|
| `sender_id` | `GROUP BY` | |
| `resync_started_event_row_id` | `max(id) FILTER (WHERE event_type = 2)` | The watermark the sweep compares against, exposed so a support query can redo the arithmetic |
| `time_resync_started` | `max(time_inserted) FILTER (WHERE event_type = 2)` | The marker's own arrival, not a trigger's `now()` |
| `time_resync_finished` | `max(time_inserted) FILTER (WHERE event_type = 3)` | While a resync is in progress this belongs to the **previous** one — read it with `resync_in_progress`, not alone |
| `resync_in_progress` | newest start marker is newer than the newest end marker | |

Nothing writes this, so it cannot disagree with the log, and it can be deleted later at no cost if nothing ends
up reading it.

### Consequences and remaining work

Called out explicitly so none of them is assumed implicit in this design:

- **`BOOLEAN` and `INTEGER` are both new to this schema** (C-13, C-14). `docs/SCHEMA_REFERENCE.md`'s type
  mapping table needs rows for them (`BOOLEAN` → `Bool`, `INTEGER` → `Int32`) even though the ClickHouse side is
  not created here.
- **ClickHouse is untouched, by direction.** Table definitions here are PostgreSQL-only in any case (C-15), and
  ClickHouse has no trigger mechanism — its equivalent would be an `AggregatingMergeTree` or
  `ReplacingMergeTree` fed by a materialized view, which belongs to the deployment that owns those tables. The
  one knowingly accepted gap: `users/` changes normally apply to both engines (C-15), so a ClickHouse
  deployment that ever exports this entity will need the log table added to `clickhouse/users/exporter.sql` and
  `ro.sql`. Until one does, there is nothing to grant on.
- **Both `post/run_on_every_build.sql` scripts name every table** in the deployment line (C-17) and need the new
  ones.
- **`MigrationHandler` needs three defaults, a new variable list and three `identifierVariables` entries**
  (C-11), and the log line that reports `tableVariables` should report the new tables too.
- **A deployment that renames the log table must pass `--variables` on every run**, as it already must for the
  five existing tables — otherwise a second set of objects appears under the defaults.
- **Documentation touch points**, per the review checklist (C-18): `docs/SCHEMA_REFERENCE.md` (three tables plus
  the type mapping), `docs/TEMPLATE_VARIABLES.md`, `docs/WRITING_MIGRATIONS.md` and `docs/CONTRIBUTING.md`
  (their variable checklists), `docs/USERS_AND_PERMISSIONS.md` (the widened exporter grants),
  `docs/FAQ.md` ("which tables does the migrator own?"), and the root `README.md`.
- **The schema is a published interface** (C-18). These three tables are new, so nothing here is breaking — but
  from the moment they ship, a column rename or removal is, and the `event_type` values become part of that
  interface too.

---

## 3. Ownership

The DDL of all three tables belongs to this repository, as every PostgreSQL export object does.

The **rows** divide differently, and the division is the point of the design:

- The log's rows are the trade server's. Nothing here writes them; the reconcile statement only reads them.
- The current-state and resync-state rows are the **trigger's**, which is to say this repository's. No
  application, no scheduled job and no operator writes them in normal operation, and the only sanctioned
  out-of-band write is re-applying the projection script (KD-5) — this repository's own code.
- The **truth** about which accounts are live stays with the trade server. The state table is a cache of that
  truth with no independent authority, so a disagreement is always resolved in the trade server's favour, by a
  resync (C-22), never by editing the state table.
- The log's retention window belongs to the deployment, through the `post/` phase. KD-1 is what makes that
  ownership harmless to the state table.

One ownership boundary genuinely moves: the exporter role gains write authority over two tables it never reads
(KD-6). It is called out here because `psql/users/exporter.sql` currently documents the opposite (C-6), and
because it is the one place where this ticket widens what the trade server's credential can do.

---

## 6. Foundations

**C-1** — `psql/20260101_01_create_trades.sql:11-12`:
`id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY` and
`time_inserted TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()` open every exported table in this repository.
The identity column is the only per-row value that orders rows within one exported batch.

**C-2** — `psql/20260101_01_create_trades.sql:51-58`: "what the index covers comes before the table name:
PostgreSQL truncates an identifier to 63 bytes, so a configured table name long enough to reach the limit would
otherwise leave both indexes with the same truncated name, and the second `CREATE … IF NOT EXISTS` would quietly
do nothing." The same rule is restated in `psql/20260101_03_create_daily_statements.sql` and
`psql/20260828_01_add_reporting_indexes.sql`.

**C-3** — `psql/20260101_02_create_export_cursor.sql:3-11`: "The cursor row and the batch of trades are written
in the same transaction, so the two can never drift apart. This table is the authoritative source of truth."
`PRIMARY KEY (sender_id, table_name)`, and no identity column — the precedent for a small state table keyed by
its natural key.

**C-4** — `docs/SCHEMA_REFERENCE.md:578-600`: at-least-once delivery is absorbed on PostgreSQL by "a `UNIQUE`
index on the key", applying "immediately, on insert", with the effect that "second insert rejected". Every key
listed leads with `sender_id`.

**C-5** — `psql/20260101_04_create_order_snapshots.sql:36-38,50` and
`psql/20260101_05_create_position_snapshots.sql:31-32`: the only nullable columns in the whole schema, and they
are nullable because the exporter itself writes `NULL` for a NaN sentinel — not because this repository
translated anything. `NULL` as "not applicable" is this repository's idiom for a column it owns.

**C-6** — `psql/users/exporter.sql:4-5,26-34`: the exporter gets `INSERT, SELECT` on each exported table and
`INSERT, SELECT, UPDATE` on the cursor, with the stated rationale "Nothing else: an exported record is never
updated or deleted."

**C-7** — `psql/users/ro.sql:25-53`: the `ro` role converges by revoking everything then granting `SELECT`, with
the note that "Revoking a privilege that was never granted is a no-op, so this is safe on a fresh role", and
that a view carries its own privileges so a grant on the table does not reach through to it.

**C-8** — `src/Migrator/Execution/DbUpRunner.cs:52-58` and `src/Migrator/Execution/ScriptSubFolders.cs:33-58`:
the five phases are a hard-coded `List<ExecutionOrder>` — `pre` (1), root (2), `views` (3), `users` (4),
`post` (5). A sixth phase is a C# change plus every doc that enumerates them
(`README.md`, `docs/WRITING_MIGRATIONS.md:13-30`, `docs/QUICKSTART.md:50`, `docs/ARCHITECTURE.md:110`).

**C-9** — `src/Migrator/Execution/DbUpRunner.cs:295-298` and `docs/WRITING_MIGRATIONS.md:101-110`: every
PostgreSQL script runs under `WithTransactionPerScript()`, and "A separator does not escape the transaction" —
`----break----` produces several commands inside the same transaction, not several independent ones.

**C-10** — `docs/WRITING_MIGRATIONS.md:59-78`: a re-runnable script is journalled with the MD5 of its
**processed** content, the previous entry is deleted first, and it re-runs "whenever its content changes — and
only then". `--force` "re-applies all of them regardless of hash, for when the *effect* was lost without the
*content* changing — someone dropped a view by hand."

**C-11** — `src/Migrator/Handlers/MigrationHandler.cs:45-78`: `tableVariables` holds the five names the trade
server writes to; `viewVariables` is "Kept apart from `tableVariables` because the trade server never writes to
a view and so has no say in its name. They are templated all the same, so a deployment that already owns an
object under one of the default names can move ours aside with `--variables`." `identifierVariables` composes
both, with the warning that "adding a table to `tableVariables` alone would otherwise leave its name
unvalidated."

**C-12** — `src/Migrator/Extensions/SqlSafety.cs:37-39`: every templated identifier must match
`^[A-Za-z_][A-Za-z0-9_]{0,62}$` — "the intersection of what PostgreSQL and ClickHouse accept as an unquoted
identifier, capped at the PostgreSQL limit of 63 bytes."

**C-13** — a `grep` for `CREATE TRIGGER`, `CREATE OR REPLACE FUNCTION`, `plpgsql`, `BOOLEAN` and `MATERIALIZED`
across `psql/`, `clickhouse/`, `docs/` and `src/Migrator.Tests/TestScripts/` returns only the two documentation
lines that mention "views and materialized views" as a phase description. There is **no** trigger, no stored
function, no `plpgsql`, no `BOOLEAN` column and no materialized view anywhere in this repository today. Both the
trigger machinery and the `BOOLEAN` columns are new ground here.

**C-14** — `docs/SCHEMA_REFERENCE.md:560-576`: the PostgreSQL-to-ClickHouse type mapping table covers `BIGINT`,
`SMALLINT`, `NUMERIC`, `VARCHAR(n)`, `TEXT`, `TIMESTAMP WITH TIME ZONE`, their nullable forms, and the identity
column. There is no row for `BOOLEAN` and none for `INTEGER`.

**C-15** — `docs/WRITING_MIGRATIONS.md:163-190`: "`clickhouse/` does **not** create the exported tables or the
cursor … So a change to the table *definitions* is a PostgreSQL-only migration here; its ClickHouse counterpart
belongs in the deployment that owns those tables, and in the test-only definitions under
`src/Migrator.Tests/TestScripts/clickhouse/` … Changes to `users/` and `post/` still apply to both."

**C-16** — `psql/20260828_01_add_reporting_indexes.sql:13-16`: "Indexes are not free on the write path — the
exporter inserts into trades continuously — so this stops at the access paths the shipped views and the
statement tables need. Per-symbol, per-group and per-position reporting are all plausible next candidates,
deliberately left out until a real workload asks for them rather than guessed at here."

**C-17** — `psql/post/run_on_every_build.sql:4` and `clickhouse/post/run_on_every_build.sql`: a single
re-runnable `SELECT` naming every table variable, so the applied configuration is visible in the migration log.

**C-18** — `docs/CONTRIBUTING.md:79-131`: the review checklist requires the table variables over literal names,
`docs/` updated for a new variable and a schema change, and a QA section; and "The schema is a published
interface for the clients running these databases. Renaming or dropping a column is a breaking change even when
nothing inside YB reads it."

**C-19** — `docker-compose.yml` (`postgres:18`), `docs/CONFIGURATION.md:248` and `docs/FAQ.md:275-279`: the
integration containers run PostgreSQL 18. A **minimum** ClickHouse version is stated (24.x or newer, because of
the driver); no minimum PostgreSQL version is stated anywhere in this repository.

**C-20** — TS-2928 design, KD-3: a delete row leaves every content column and the `version` column at a
type-appropriate default; every column stays `NOT NULL`; no `CHECK` constraint is added. This mirrors
`TradingAccountConfig`'s own sentinel-value idiom for optionality — fixed-width scalars with no `std::optional`
at the wire level.

**C-21** — TS-2928 design, KD-4: a resync is `event_type = 'snapshot_start'` ("discard everything previously
known for this sender"), an unbounded stream of ordinary upserts, then `event_type = 'snapshot_end'`. No pass
id, no total-row-count, no per-row sequence number — correlation is "established by position in the per-sender
ordered stream alone". Both markers' own `entity_id` and `entity_version` carry KD-3's zero defaults. Neither
the markers nor the re-announcements between them are logical changes to any account, so a consumer tracking
account changes has to filter them out.

**C-22** — TS-2928 design, KD-5: a resync runs automatically once per trade server startup, but only for
pipelines whose configuration sets `full_resync_on_startup`, which **defaults to `false`** — and
"TradingAccount's own pipeline entry in `export_config.json` must explicitly set `full_resync_on_startup: true`
when this ships, or the feature this ticket implements silently does not run." The whole mechanism is
"explicitly interim"; an admin-triggered resync is the likely long-term direction, deferred to a follow-up.

**C-23** — TS-2928 SPEC: events for a given account are recorded in order and none is observable after its
deletion; each logical change produces exactly one event; a state-carrying event always carries full state,
never a delta; non-delete events are recorded in monotonically increasing `version` order; reconstruction with
no gaps keys off **recorded order**, not `version`; "Events may be purged once they age past a bounded retention
window"; "A resync is not guaranteed to be written atomically. It may be split across multiple transactions";
and the live account count is bounded at roughly 100,000.

**The monotonic-`version` invariant above does not hold as written, given C-30.** If a trading account id is
reused after deletion, that id's non-delete events run `v1, v2, v3, …` and then start again at `v1`, so
`version` is monotonic only *within one incarnation of an id*, not across the id's history in the log. Nothing
in this design depends on the invariant — the projection never compares `version` (KD-3) and orders by arrival
— so the contradiction costs nothing here, but it is quoted from TS-2928's SPEC and is worth resolving with
that team: either the invariant is implicitly per-incarnation and should say so, or one of the two statements
is wrong. Anyone who builds on the invariant as written will get it wrong for a reused id; the monitoring query
in this design resets its version window at each deletion for exactly this reason.

**C-24** — TS-2928 design, KD-1 and its C-1/C-2: the event log is one table per entity with an `event_type`
discriminator and all content columns present on every row; the `COPY` command is built once from one table name
and one descriptor's column list, so the pipeline is single-table, single-`COPY`-per-batch, and the column list
is explicit — an identity or defaulted column the exporter does not name is simply left to the table.

**C-25** — the trading account API surface quoted in the ticket: `id`, `version`, `groupId`, `clientId`,
`leverage` (`int32`, `>= 1`), `enabled` and `allowTrading` are required; `introducer`,
`timePasswordLastChanged`, `externalAccountId` (1–63 characters), `comment` (1–255 characters), `timeCreated`
and `timeModified` are optional; timestamps are microseconds since the Unix epoch; and `password` "is never
present in responses to GET requests."

**C-26** — the PostgreSQL behaviour this design depends on, none of which has a precedent in this repository to
point at (C-13), so all of it needs proving in the integration tests rather than assumed: `COPY FROM` invokes
row-level triggers on the destination table; `AFTER … FOR EACH ROW` triggers are queued and fired at the end of
the statement in the order the rows were processed; `now()` returns transaction-start time, so every row of one
batch shares a `DEFAULT now()` value; a unique-index violation inside a `COPY` aborts the whole command and its
transaction; `CREATE OR REPLACE TRIGGER` requires PostgreSQL 14 and transition tables
(`REFERENCING NEW TABLE`) require PostgreSQL 10; and both `DROP TRIGGER` and `CREATE TRIGGER` take a lock that
conflicts with the `INSERT` an exporting `COPY` needs.

**C-27** — DbUp's own variable substitution claims `$name$`, so a custom dollar-quote tag is unusable in a
script here. `CREATE FUNCTION … AS $projection$ … $projection$` never reaches the server: the run fails with
`Upgrade failed due to an unexpected exception: System.InvalidOperationException: Variable projection has no
value defined`. This is DbUp core's preprocessor, separate from the migrator's own `##name##` substitution
(`DbUpRunner.SubstituteTemplateVariables`), and it is why the one existing dollar-quoted block in this
repository — the `DO $$ … $$` in `psql/users/exporter.sql` — uses a bare `$$`. Observed, not inferred.

**C-28** — in `COPY` text format an empty field is an empty string and `\N` is `NULL`, so a writer that emits
`\N` for an absent value drops a `NULL` into a `NOT NULL` column and aborts the whole command:
`ERROR: null value in column "external_account_id" of relation "trading_account_events" violates not-null
constraint`. TS-2928's KD-2 requires new default-writing siblings to `PgWriter`'s existing NULL-only helpers
(its C-19) precisely so this cannot happen; if that part is missed, every delete row and every resync marker
fails on the first string column. Observed against `postgres:18`.

**C-30** — trading account ids are reused after deletion, confirmed by the ticket owner, including the case
where the reuse is only observed through a full resync. Two consequences, both verified against `postgres:18`:
a version sequence restarts, so `version` cannot gate an upsert (KD-3); and monitoring that checks version
continuity has to reset at each deletion event rather than treat a regression as an anomaly. A third
consequence needed checking and turned out safe: a purge cannot leave the *old* incarnation's state as the
newest retained event for a reused id, because the deletion and the new account's creation are both newer and a
purge by age drops the oldest first — the same structural argument that makes the resync watermark safe (KD-4).
That holds for prefix-by-age purging, not for a selective delete.

**C-29** — verified against the `postgres:18` container in `docker-compose.yml`, as the `trade_exporter` and
`trade_reader` roles, in separate transactions to mimic batches: a clean install applies and a second run
applies nothing; an account created and deleted inside one statement ends absent; a `COPY` fires the row
trigger; the resync start marker leaves the state table complete while the resync view reports it in progress;
the end marker sweeps exactly the accounts that were not re-announced; a stray end marker deletes nothing; an
unrecognised `event_type` is stored without raising; the same event inserted twice leaves two log rows and one
correct state row; a batch carrying an end marker followed by a later resync's start marker sweeps against the
right watermark and keeps both live accounts; dropping the trigger by hand and running `--force` restores it
and heals the drift without resurrecting a swept account; and `ro` can read all three objects but write none of
them.
