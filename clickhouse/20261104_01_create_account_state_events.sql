----break----

-- Account states exported by the trade server: a change stream with one row per change of a trading account's
-- state, plus the markers that bracket each full snapshot. The protocol is the one of position_events, with
-- trading_account_id in the role of position_id. The current state is read from the views over this table:
-- account_states_live, computed on every read (views/account_states_live.sql), and account_states, the same
-- rows refreshed on a schedule (post/account_states.sql).
--
-- Event types:
--   Upsert        - the account state as it now stands
--   Delete        - the trading account was deleted; the row carries its last known state
--   SnapshotStart - a full snapshot of the sender's account states begins; trading_account_id = 0
--   SnapshotEnd   - the snapshot is complete; trading_account_id = 0
--
-- Column types must match what the exporter writes exactly: the native protocol rejects a block whose
-- column type differs from the table's.
--
-- Versioning: ReplacingMergeTree keeps the row with the highest sequence for each (sender_id, trading_account_id),
-- so a row with a lower sequence never replaces one with a higher, whatever order they arrive in. Until the parts
-- merge a query sees every version unless it reads with FINAL.
--
-- Markers: every snapshot marker carries trading_account_id = 0, so marker_sequence, which is the sequence for a
-- marker and 0 for an account, keeps each marker distinct under the sorting key and markers never replace each
-- other.
--
-- Partitioning by sender_id keeps the senders apart, so FINAL can work one partition at a time.
--
-- Merges and retention are not part of the definition: post/account_state_events_ttl.sql sets
-- min_age_to_force_merge_seconds, which force-merges the parts of a partition whether or not new rows arrive, and
-- then the TTL that removes Delete rows and relies on those merges. The post phase always runs after this script,
-- and the script there re-runs whenever either value changes.
--
-- The name is qualified with the database because ON CLUSTER carries the statement to nodes whose default
-- database is not this one.

CREATE TABLE IF NOT EXISTS ##database_name##.##account_state_events_table## ##cluster_name_suffix##
(
    -- Service columns
    sender_id                                UInt64                                                                       NOT NULL,
    sequence                                 UInt64                                                                       NOT NULL,
    batch_time                               DateTime64(6, 'UTC')                                                         NOT NULL,
    event_type                               Enum8('Upsert' = 0, 'Delete' = 1, 'SnapshotStart' = 2, 'SnapshotEnd' = 3) NOT NULL,

    trading_account_id                       UInt64                                                                       NOT NULL,
    group_id                                 UInt64                                                                       NOT NULL,
    account_currency                         LowCardinality(String)                                                       NOT NULL,
    account_currency_decimal_places          UInt8                                                                        NOT NULL,

    balance                                  Decimal(38, 12)                                                              NOT NULL,
    credit                                   Decimal(38, 12)                                                              NOT NULL,
    equity                                   Decimal(38, 12)                                                              NOT NULL,
    margin                                   Decimal(38, 12)                                                              NOT NULL,
    otc_floating_pl                          Decimal(38, 12)                                                              NOT NULL,
    otc_realized_pl                          Decimal(38, 12)                                                              NOT NULL,
    collateral                               Decimal(38, 12)                                                              NOT NULL,
    is_margin_call                           Bool                                                                         NOT NULL,

    -- the sequence for a snapshot marker, 0 for an account; never written by the exporter
    marker_sequence                          UInt64 MATERIALIZED if(trading_account_id = 0, sequence, 0)
)
ENGINE = ##replacing_merge_tree_engine##(sequence)
PARTITION BY sender_id
ORDER BY (sender_id, trading_account_id, marker_sequence);
