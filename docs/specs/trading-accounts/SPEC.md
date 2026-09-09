# SPEC — [PSQL] Trading Accounts snapshot table and triggers

## Overview
The trade server exports trading account changes into a PostgreSQL event log (TS-2928). Beside it, this
repository keeps a **current-state table**: one row per live account, maintained by database triggers.
Reporting reads that table directly instead of replaying the log.

## Workflows
- The migrator creates the event log, the current-state table, and the grants both need.
- A state event creates or updates the account's row. A deletion event removes it.
- A resync — start marker, stream of state events, end marker — converges the table on the live set that resync
  listed.
- A consumer can read, per sender, whether a resync is in progress.
- An operator re-applies the projection to bring the table back in line with the log.

## Edge Cases
- Several events for one account in one batch: the last one wins.
- A deletion for an account with no row: no effect.
- An end marker with no start marker: nothing is removed.
- A resync that never finishes: accounts already re-announced are current, the rest keep their previous state,
  and the sender stays flagged as resync-in-progress.
- An unrecognised event type: stored in the log, not projected, and no batch fails.
- A redelivered event: the state ends up as if it had arrived once.
- An id reused by a different account: the row shows the new account, whether the deletion arrived first or the
  reuse is only seen when a resync re-announces the id.
- Re-applying the projection: an account a finished resync removed is not brought back.
- Two senders in one database: neither affects the other's rows.

## Invariants
- The current-state table always matches the committed log — no lag, no catching up.
- One row per (sender, trading account), at most.
- Fields are copied from the event unchanged; nothing is interpreted or converted.
- Every row records which log event produced it.
- The projection gives an export batch no new way to fail.
- Purging old events cannot change the current state.
- Nothing has to be scheduled — the trade server's write is the only input.
- No credential material reaches PostgreSQL. The account password is never exported or stored.

## Assumptions
- This repository creates the event log, as it does every PostgreSQL table the trade server exports into.
- The stream behaves as TS-2928 specifies: ordered per sender, one event per change, full state on every state
  event, deletion last, replayed by arrival order rather than by `version`.
- The exporter writes every column on every row, using zeros where a field does not apply.
- A trading account id may be reused after deletion, so a version sequence restarts and `version` cannot be
  used to decide whether an event is newer than the stored state.
- Live accounts are bounded at roughly 100,000, and account changes are rare next to trades.
- The log's retention window belongs to the deployment; the current state must not depend on it.
- A resync is the authoritative repair. Anything rebuilt locally from the log is best-effort.
- Consumers only read the current state.
- ClickHouse is out of scope: no tables, no views, no grants.
