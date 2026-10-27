----break----

-- Positions exported by the trade server: a change stream with one row per change of an open position, plus the
-- markers that bracket each full snapshot. The current state is read from the views over this table:
-- positions_state_live, computed on every read (views/positions_state_live.sql), and positions_state, the same
-- rows refreshed on a schedule (post/positions_state.sql).
--
-- Event types:
--   Upsert        - the position as it now stands
--   Delete        - the position was closed; the row carries its last known values
--   SnapshotStart - a full snapshot of the sender's open positions begins; position_id = 0
--   SnapshotEnd   - the snapshot is complete; position_id = 0
--
-- Column types must match the exporter's PositionDescriptor exactly: the native protocol rejects a block whose
-- column type differs from the table's.
--
-- Versioning: ReplacingMergeTree keeps the row with the highest sequence for each (sender_id, position_id), so a
-- re-sent or late batch never overwrites a newer change. sequence grows monotonically per sender. Until the parts
-- merge a query sees every version unless it reads with FINAL.
--
-- Markers: every snapshot marker carries position_id = 0, so marker_sequence, which is the sequence for a marker
-- and 0 for a position, keeps each marker distinct under the sorting key and markers never replace each other.
--
-- Partitioning by sender_id keeps the senders apart, so FINAL can work one partition at a time.
--
-- Merges and retention are not part of the definition: post/positions_ttl.sql sets min_age_to_force_merge_seconds,
-- which force-merges the parts of a partition whether or not new rows arrive, and then the TTL that removes Delete
-- rows and relies on those merges. The post phase always runs after this script, and the script there re-runs
-- whenever either value changes.
--
-- The name is qualified with the database because ON CLUSTER carries the statement to nodes whose default
-- database is not this one.

CREATE TABLE IF NOT EXISTS ##database_name##.##positions_table## ##cluster_name_suffix##
(
    -- Service columns
    sender_id                       UInt64                                                                       NOT NULL,
    sequence                        UInt64                                                                       NOT NULL,
    batch_time                      DateTime64(6, 'UTC')                                                         NOT NULL,
    event_type                      Enum8('Upsert' = 0, 'Delete' = 1, 'SnapshotStart' = 2, 'SnapshotEnd' = 3) NOT NULL,

    position_id                     UInt64                                                                       NOT NULL,
    trading_account_id              UInt64                                                                       NOT NULL,
    group_id                        UInt64                                                                       NOT NULL,
    group_name                      LowCardinality(String)                                                       NOT NULL,
    symbol_id                       UInt64                                                                       NOT NULL,
    symbol_name                     LowCardinality(String)                                                       NOT NULL,

    side                            Enum8('buy' = 0, 'sell' = 1)                                                 NOT NULL,
    volume                          Decimal(18, 8)                                                               NOT NULL,
    lot_size                        Float64                                                                      NOT NULL,
    avg_open_price                  Decimal(38, 12)                                                              NOT NULL,

    stop_loss                       Decimal(38, 12)                                                              NOT NULL,
    stop_loss_order_id              UInt64                                                                       NOT NULL,
    take_profit                     Decimal(38, 12)                                                              NOT NULL,
    take_profit_order_id            UInt64                                                                       NOT NULL,

    close_market_price              Decimal(38, 12)                                                              NOT NULL,
    close_group_markup              Int32                                                                        NOT NULL,
    pl                              Decimal(38, 8)                                                               NOT NULL,
    margin                          Decimal(38, 8)                                                               NOT NULL,
    asset_or_liability              Decimal(38, 8)                                                               NOT NULL,
    profit_conversion_rate          Float64                                                                      NOT NULL,
    margin_conversion_rate          Float64                                                                      NOT NULL,

    swaps                           Decimal(38, 8)                                                               NOT NULL,
    commission                      Decimal(38, 8)                                                               NOT NULL,
    fee                             Decimal(38, 8)                                                               NOT NULL,

    price_decimal_places            UInt8                                                                        NOT NULL,
    account_currency_decimal_places UInt8                                                                        NOT NULL,

    created                         DateTime64(6, 'UTC')                                                         NOT NULL,
    created_by_order_id             UInt64                                                                       NOT NULL,
    updated                         DateTime64(6, 'UTC')                                                         NOT NULL,
    updated_by_order_id             UInt64                                                                       NOT NULL,

    -- the sequence for a snapshot marker, 0 for a position; never written by the exporter
    marker_sequence                 UInt64 MATERIALIZED if(position_id = 0, sequence, 0)
)
ENGINE = ##replacing_merge_tree_engine##(sequence)
PARTITION BY sender_id
ORDER BY (sender_id, position_id, marker_sequence);
