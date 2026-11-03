-- Account states exported by the trade server: a change stream with one row per change of a trading account's
-- state, plus the markers that bracket each full snapshot. The protocol is the one of position_events, with
-- trading_account_id in the role of position_id.
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
-- min_age_to_force_merge_seconds force-merges the parts of a partition once every part is that old, with or
-- without new inserts, so the versions of one account collapse within minutes. The TTL removes Delete rows
-- 30 days after their batch_time and is safe only because of those merges: a Delete row removed before the
-- merge collapsed the account into it would let the account's last Upsert win again.

CREATE TABLE account_state_events
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

    prev_day_balance                         Decimal(38, 12)                                                              NOT NULL,
    prev_month_balance                       Decimal(38, 12)                                                              NOT NULL,
    prev_day_credit                          Decimal(38, 12)                                                              NOT NULL,
    prev_month_credit                        Decimal(38, 12)                                                              NOT NULL,
    prev_day_equity                          Decimal(38, 12)                                                              NOT NULL,
    prev_month_equity                        Decimal(38, 12)                                                              NOT NULL,
    prev_day_collateral                      Decimal(38, 12)                                                              NOT NULL,
    prev_month_collateral                    Decimal(38, 12)                                                              NOT NULL,

    current_day_otc_realized_pl              Decimal(38, 12)                                                              NOT NULL,
    current_month_otc_realized_pl            Decimal(38, 12)                                                              NOT NULL,
    current_day_commissions                  Decimal(38, 12)                                                              NOT NULL,
    current_month_commissions                Decimal(38, 12)                                                              NOT NULL,
    current_day_agent_commissions            Decimal(38, 12)                                                              NOT NULL,
    current_month_agent_commissions          Decimal(38, 12)                                                              NOT NULL,
    current_day_fees                         Decimal(38, 12)                                                              NOT NULL,
    current_month_fees                       Decimal(38, 12)                                                              NOT NULL,
    accumulated_daily_commissions            Decimal(38, 12)                                                              NOT NULL,
    accumulated_monthly_commissions          Decimal(38, 12)                                                              NOT NULL,
    accumulated_daily_fees                   Decimal(38, 12)                                                              NOT NULL,
    accumulated_monthly_fees                 Decimal(38, 12)                                                              NOT NULL,
    current_day_closed_positions_swaps       Decimal(38, 12)                                                              NOT NULL,
    current_month_closed_positions_swaps     Decimal(38, 12)                                                              NOT NULL,
    current_day_interest                     Decimal(38, 12)                                                              NOT NULL,
    current_month_interest                   Decimal(38, 12)                                                              NOT NULL,
    current_day_deposits                     Decimal(38, 12)                                                              NOT NULL,
    current_month_deposits                   Decimal(38, 12)                                                              NOT NULL,
    current_day_withdrawals                  Decimal(38, 12)                                                              NOT NULL,
    current_month_withdrawals                Decimal(38, 12)                                                              NOT NULL,
    current_day_credit_transactions          Decimal(38, 12)                                                              NOT NULL,
    current_month_credit_transactions        Decimal(38, 12)                                                              NOT NULL,
    current_day_adjustment_transactions      Decimal(38, 12)                                                              NOT NULL,
    current_month_adjustment_transactions    Decimal(38, 12)                                                              NOT NULL,
    current_day_balance_bonus_transactions   Decimal(38, 12)                                                              NOT NULL,
    current_month_balance_bonus_transactions Decimal(38, 12)                                                              NOT NULL,
    current_day_credit_bonus_transactions    Decimal(38, 12)                                                              NOT NULL,
    current_month_credit_bonus_transactions  Decimal(38, 12)                                                              NOT NULL,
    current_day_dividends                    Decimal(38, 12)                                                              NOT NULL,
    current_month_dividends                  Decimal(38, 12)                                                              NOT NULL,
    current_day_tax                          Decimal(38, 12)                                                              NOT NULL,
    current_month_tax                        Decimal(38, 12)                                                              NOT NULL,
    current_day_stopout_compensations        Decimal(38, 12)                                                              NOT NULL,
    current_month_stopout_compensations      Decimal(38, 12)                                                              NOT NULL,
    current_day_stopout_credit_adjustments   Decimal(38, 12)                                                              NOT NULL,
    current_month_stopout_credit_adjustments Decimal(38, 12)                                                              NOT NULL,
    current_day_volume_in_usd                Decimal(38, 12)                                                              NOT NULL,
    current_month_volume_in_usd              Decimal(38, 12)                                                              NOT NULL,
    current_day_volume_in_account_currency   Decimal(38, 12)                                                              NOT NULL,
    current_month_volume_in_account_currency Decimal(38, 12)                                                              NOT NULL,

    -- the sequence for a snapshot marker, 0 for an account; never written by the exporter
    marker_sequence                          UInt64 MATERIALIZED if(trading_account_id = 0, sequence, 0)
)
ENGINE = ReplacingMergeTree(sequence)
PARTITION BY sender_id
ORDER BY (sender_id, trading_account_id, marker_sequence)
TTL toDateTime(batch_time) + INTERVAL 30 DAY DELETE WHERE event_type = 'Delete'
SETTINGS min_age_to_force_merge_seconds = 600;
