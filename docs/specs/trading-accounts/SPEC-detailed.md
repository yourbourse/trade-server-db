# SPEC — [PSQL] Trading Accounts snapshot table and triggers

## Overview
PostgreSQL receives the `TradingAccount` event log the trade server exports (TS-2928). This repository creates
that log table and, beside it, a **current-state table** holding one row per live trading account, derived from
the log by PostgreSQL itself. A reporting consumer reads the current state directly: it does not replay the log,
does not track resync markers, and does not keep a projection of its own.

## Workflows
- The migrator creates the trading account event log the trade server's export pipeline writes to, the
  current-state table derived from it, and the read and write grants both need.
- When the trade server records an event carrying account state, that account's current-state row is created or
  updated as part of the same write.
- When the trade server records a deletion event, that account's current-state row is removed as part of the
  same write.
- When the trade server records a resync — `snapshot_start`, a stream of state-carrying events, then
  `snapshot_end` — the current-state table converges on the live set that resync enumerated.
- A consumer reads, per sender, whether a resync is currently in progress, so it can tell a converging picture
  from a settled one.
- An operator re-applies the projection to bring the current-state table back in line with the retained log —
  after installing it, after changing it, or after the table was altered by hand.

## Edge Cases
- Several events for one account inside one exported batch: the account's row ends up as the last of them left
  it, in the log's recorded order.
- A deletion for an account that has no current-state row: no effect.
- `snapshot_end` with no `snapshot_start` before it for that sender: no rows are removed.
- A second `snapshot_start` before the previous resync ended: the later resync supersedes the earlier one, and
  only accounts re-announced under the later one survive its `snapshot_end`.
- Markers belonging to two different resyncs inside one exported batch: each `snapshot_end` removes only what
  its own resync failed to re-announce, never what a later resync has yet to reach.
- A resync that never reaches `snapshot_end` — the sender died partway: accounts it had already re-announced are
  current; accounts it had not reached, and accounts deleted while the sender was down, remain visible. The
  sender stays marked as resync-in-progress until a later resync completes.
- An event type the projection does not recognise: recorded in the log, absent from the current state, and no
  export batch fails because of it.
- A redelivered event: the current state ends up as if it had arrived once. The log keeps both copies, so a
  consumer reading the log directly can see one logical change twice.
- An id reused by a different account: the current state shows the new account. This holds whether the deletion
  arrives before the reuse, or the reuse is first seen when a resync re-announces the id with no deletion event
  ever arriving.
- Events aged out of the log's retention window: the current state is unaffected.
- Re-applying the projection over the retained log: an account that a completed resync removed is not brought
  back, even though nothing in the log records its removal — only its absence from that resync does.
- Purging the log, in whole or in part: the current state is unaffected.
- The current-state table is empty or absent when the projection is installed: applying the projection populates
  it from whatever the log still holds. What the log no longer holds can only come from a resync.
- The trade server writes while the projection is being installed or replaced: no event is projected twice, and
  no event is silently skipped.
- Two senders exporting into one database: neither sender's events affect the other's rows, and neither sender's
  resync affects the other's.
- The exported account content grows a field: the log and the current state both carry it from the migration
  that adds it onwards; rows written earlier carry that field's default, not its real value.

## Invariants
- The current-state table is the reduction of the committed event log at every transaction boundary. A consumer
  never observes state the log does not account for, and never waits for the projection to catch up.
- A single read of the current-state table returns one point-in-time set, never a half-applied batch.
- At most one row per (sender, trading account). Reading the table is the complete answer to "which accounts are
  live, and what do they look like".
- Every field a state-carrying event carries is present in the current-state row unaltered. The projection
  copies; it does not interpret, convert, aggregate or re-derive.
- Every current-state row names the log event that produced it.
- The projection adds no new reason for an export batch to fail: any event the log accepts is projected.
- The current state outlives the log. Nothing about the retention window is allowed to change it.
- Nothing has to be scheduled, polled or triggered by an operator for the current state to be right. The trade
  server's own write is the only input.
- The projection is re-appliable: applying it again over the same retained log reaches the same result.
- Reading the current state needs no privilege beyond what reading the exported tables needs today. Maintaining
  it needs no privilege the export does not already hold.
- No credential material reaches PostgreSQL. The trading account password is neither exported nor stored.

## Assumptions
- The event log table is this repository's to create, on PostgreSQL, as is every other table the trade server
  exports into.
- The exported stream behaves as TS-2928 specifies: ordered per sender, one event per logical change, full state
  on every state-carrying event, deletion last, and reconstruction keyed off recorded order rather than
  `version`.
- The live account count is bounded — around 100,000 per TS-2928 — so the current-state table is small enough to
  read without special handling and a resync is a batch of that order, not an unbounded stream.
- Account changes are rare next to trades, so the projection's cost is concentrated in resyncs rather than in
  steady-state traffic.
- The log's retention window belongs to the deployment. This SPEC constrains only that the current state must
  not depend on it.
- A resync from the trade server is the authoritative repair for a current state that has drifted. Anything this
  repository can reconstruct from the retained log is best-effort and bounded by that window.
- Consumers only read the current state. Nothing outside this repository writes to it.
- The exporter writes every column on every row, using type-appropriate zeros where a field does not apply.
- A trading account id may be reused by a different account after the original is deleted. A version sequence
  therefore restarts, and `version` cannot be used to decide whether an event is newer than the stored state.
- The exported event carries the account's fields individually rather than as one opaque document, so the
  database can type them, constrain them, and refuse to hold a field it was never told about — the account
  password in particular.
- ClickHouse is out of scope entirely: no tables, no views and no grants. It has no trigger mechanism, and it
  does not create the exported tables here either. One consequence is accepted knowingly — a ClickHouse
  deployment that ever exports this entity needs its own current-state mechanism and its own grants, as
  separate work.
