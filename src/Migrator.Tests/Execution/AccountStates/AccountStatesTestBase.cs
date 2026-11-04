// /********************************************************************
//  *
//  * CONFIDENTIAL
//  * __________________
//  *
//  * Copyright (c)  Your Bourse Limited
//  * All Rights Reserved.
//  *
//  * NOTICE:  All information contained herein is, and remains
//  * the property of Your Bourse Limited. The intellectual and
//  * technical concepts contained herein are proprietary to Your
//  * Bourse Limited and may be covered by UK and Foreign Patents,
//  * patents in process, and are protected by trade secret or
//  * copyright law. Dissemination of this information or reproduction
//  * of this material is strictly forbidden unless prior written
//  * permission is obtained from Your Bourse Limited.
//  */


using Xunit.Abstractions;
using YB.TradeServer.Db.Migrator.Execution;

namespace YB.TradeServer.Db.Migrator.Tests.Execution.AccountStates;

/// <summary>
/// The vocabulary the account states suites are written in, over a throwaway ClickHouse database per test.
/// </summary>
/// <remarks>
/// Pins ClickHouse's behaviour under our DDL. Each test gets its own database, users and roles.
/// </remarks>
public abstract class AccountStatesTestBase(ConfigBuilder configBuilder, ITestOutputHelper output) : ClickHouseTestBase(configBuilder, output)
{
    /// <summary>
    /// Every column the exporter writes, in the table's order; marker_sequence is materialized by the server.
    /// </summary>
    protected const string AccountStateColumns =
        "sender_id, sequence, batch_time, event_type, " +
        "trading_account_id, group_id, account_currency, account_currency_decimal_places, " +
        "balance, credit, equity, margin, otc_floating_pl, otc_realized_pl, collateral, is_margin_call, " +
        "prev_day_balance, prev_month_balance, prev_day_credit, prev_month_credit, prev_day_equity, " +
        "prev_month_equity, prev_day_collateral, prev_month_collateral, current_day_otc_realized_pl, " +
        "current_month_otc_realized_pl, current_day_commissions, current_month_commissions, " +
        "current_day_agent_commissions, current_month_agent_commissions, current_day_fees, current_month_fees, " +
        "accumulated_daily_commissions, accumulated_monthly_commissions, accumulated_daily_fees, " +
        "accumulated_monthly_fees, current_day_closed_positions_swaps, current_month_closed_positions_swaps, " +
        "current_day_interest, current_month_interest, current_day_deposits, current_month_deposits, " +
        "current_day_withdrawals, current_month_withdrawals, current_day_credit_transactions, " +
        "current_month_credit_transactions, current_day_adjustment_transactions, " +
        "current_month_adjustment_transactions, current_day_balance_bonus_transactions, " +
        "current_month_balance_bonus_transactions, current_day_credit_bonus_transactions, " +
        "current_month_credit_bonus_transactions, current_day_dividends, current_month_dividends, " +
        "current_day_tax, current_month_tax, current_day_stopout_compensations, " +
        "current_month_stopout_compensations, current_day_stopout_credit_adjustments, " +
        "current_month_stopout_credit_adjustments, current_day_volume_in_usd, current_month_volume_in_usd, " +
        "current_day_volume_in_account_currency, current_month_volume_in_account_currency";

    /// <summary>
    /// The number of money columns after is_margin_call: the prev_day_*/prev_month_* values and the current_*,
    /// accumulated_* accumulators. A row fills them all with zero.
    /// </summary>
    private const int TrailingMoneyColumns = 48;

    /// <summary>
    /// The refresh period of a test that must not see a scheduled refresh: an hour, far longer than any test.
    /// </summary>
    protected const int NoScheduledRefresh = 3600;

    /// <summary>
    /// The batch_time a row gets when the test does not care: an hour back, well inside the retention.
    /// </summary>
    private static readonly DateTime defaultBatchTime = DateTime.UnixEpoch.AddSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddHours(-1);

    private static readonly string trailingZeros = string.Join(", ", Enumerable.Repeat("0", TrailingMoneyColumns));

    // ---------------------------------------------------------------------------------------------------
    // Migrating
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Provisions and migrates a database; returns its connection string.
    /// </summary>
    /// <param name="variables">--variables entries, such as <c>account_state_events_delete_ttl_days=7</c>.</param>
    protected string Migrate(params string[] variables)
    {
        var request = CreateRequest(variables);

        AssertSuccessful(CreateRunner().Run(request));

        return request.ConnectionString;
    }

    protected DbMigratorRequest CreateRequest(params string[] variables)
    {
        return CreateClickHouseRequest("account_states_integration_", variables);
    }

    /// <summary>
    /// The same request with these --variables in place of the ones it had.
    /// </summary>
    protected static DbMigratorRequest WithVariables(DbMigratorRequest request, params string[] variables)
    {
        return request with { Variables = ClickHouseVariables(request.DatabaseName, variables) };
    }

    // ---------------------------------------------------------------------------------------------------
    // Writing the change stream, as the exporter does
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// One row of the change stream. The columns no test looks at carry fixed values; markers carry them too.
    /// </summary>
    protected sealed record Row(
        ulong Sequence,
        string EventType = "Upsert",
        ulong TradingAccountId = 1,
        ulong SenderId = Sender,
        string Balance = "1",
        DateTime? BatchTime = null)
    {
        public string ToSql()
        {
            var batchTime = Time(BatchTime ?? defaultBatchTime);

            return $"({SenderId}, {Sequence}, {batchTime}, {Quote(EventType)}, " +
                   $"{TradingAccountId}, 10, 'USD', 2, " +
                   $"{Balance}, 0, {Balance}, 0, 0, 0, 0, false, " +
                   $"{trailingZeros})";
        }
    }

    protected static Row Upsert(ulong sequence, ulong tradingAccountId, string balance = "1", ulong senderId = Sender)
    {
        return new(sequence, "Upsert", tradingAccountId, senderId, balance);
    }

    protected static Row Delete(ulong sequence, ulong tradingAccountId, ulong senderId = Sender)
    {
        return new(sequence, "Delete", tradingAccountId, senderId);
    }

    protected static Row SnapshotStart(ulong sequence, ulong senderId = Sender)
    {
        return new(sequence, "SnapshotStart", 0, senderId);
    }

    protected static Row SnapshotEnd(ulong sequence, ulong senderId = Sender)
    {
        return new(sequence, "SnapshotEnd", 0, senderId);
    }

    /// <summary>
    /// A complete snapshot from <paramref name="start"/> on: the start marker, one Upsert per account, the end marker.
    /// </summary>
    protected static Row[] Snapshot(ulong start, ulong senderId, params ulong[] tradingAccountIds)
    {
        return
        [
            SnapshotStart(start, senderId),
            .. tradingAccountIds.Select((tradingAccountId, index) => Upsert(start + 1 + (ulong)index, tradingAccountId, senderId: senderId)),
            SnapshotEnd(start + 1 + (ulong)tradingAccountIds.Length, senderId)
        ];
    }

    protected static string InsertInto(string table, params Row[] rows)
    {
        return $"INSERT INTO {table} ({AccountStateColumns}) VALUES {string.Join(", ", rows.Select(row => row.ToSql()))}";
    }

    protected static string InsertSql(params Row[] rows)
    {
        return InsertInto("account_state_events", rows);
    }

    /// <summary>
    /// One statement, one batch.
    /// </summary>
    protected static void Insert(string connectionString, params Row[] rows)
    {
        Execute(connectionString, InsertSql(rows));
    }

    /// <summary>
    /// Runs the TTL pass now rather than on its schedule (merge_with_ttl_timeout).
    /// </summary>
    protected static void ApplyRetention(string connectionString)
    {
        Execute(connectionString, "ALTER TABLE account_state_events MATERIALIZE TTL SETTINGS mutations_sync = 2");
    }

    /// <summary>
    /// Refreshes the view now rather than on its schedule, and waits for the refresh to finish.
    /// </summary>
    protected static void Refresh(string connectionString, string view = "account_states")
    {
        Execute(connectionString, $"SYSTEM REFRESH VIEW {view}");
        Execute(connectionString, $"SYSTEM WAIT VIEW {view}");
    }

    // ---------------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The view as text, one "sender/account: balance" per account.
    /// </summary>
    protected static List<string> State(string connectionString, string view = "account_states")
    {
        return Strings(connectionString,
            $"SELECT concat(toString(sender_id), '/', toString(trading_account_id), ': ', toString(balance)) FROM {view} ORDER BY sender_id, trading_account_id");
    }

    /// <summary>
    /// Refreshes account_states and asserts both views show the expected accounts: the stored state after a
    /// refresh is the live one.
    /// </summary>
    protected static void AssertState(string connectionString, IEnumerable<string> expected)
    {
        Refresh(connectionString);

        var accounts = expected.ToList();

        Assert.Equal(accounts, State(connectionString, "account_states_live"));
        Assert.Equal(accounts, State(connectionString));
    }

    /// <summary>
    /// The sequences of every row on disk, merged or not.
    /// </summary>
    protected static List<long> Sequences(string connectionString)
    {
        return Longs(connectionString, "SELECT sequence FROM account_state_events ORDER BY sender_id, sequence");
    }

    // ---------------------------------------------------------------------------------------------------
    // The catalog
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The table the migration leaves, as SHOW CREATE TABLE prints it: the DDL in ClickHouse's canonical form.
    /// </summary>
    protected static string AgreedTable(string table = "account_state_events", int deleteTtlDays = 30, int forceMergeSeconds = 600)
    {
        // the raw string takes this file's line endings, which a Windows checkout makes CRLF
        return $"""
                CREATE TABLE {table}
                (
                    `sender_id` UInt64,
                    `sequence` UInt64,
                    `batch_time` DateTime64(6, 'UTC'),
                    `event_type` Enum8('Upsert' = 0, 'Delete' = 1, 'SnapshotStart' = 2, 'SnapshotEnd' = 3),
                    `trading_account_id` UInt64,
                    `group_id` UInt64,
                    `account_currency` LowCardinality(String),
                    `account_currency_decimal_places` UInt8,
                    `balance` Decimal(38, 12),
                    `credit` Decimal(38, 12),
                    `equity` Decimal(38, 12),
                    `margin` Decimal(38, 12),
                    `otc_floating_pl` Decimal(38, 12),
                    `otc_realized_pl` Decimal(38, 12),
                    `collateral` Decimal(38, 12),
                    `is_margin_call` Bool,
                    `prev_day_balance` Decimal(38, 12),
                    `prev_month_balance` Decimal(38, 12),
                    `prev_day_credit` Decimal(38, 12),
                    `prev_month_credit` Decimal(38, 12),
                    `prev_day_equity` Decimal(38, 12),
                    `prev_month_equity` Decimal(38, 12),
                    `prev_day_collateral` Decimal(38, 12),
                    `prev_month_collateral` Decimal(38, 12),
                    `current_day_otc_realized_pl` Decimal(38, 12),
                    `current_month_otc_realized_pl` Decimal(38, 12),
                    `current_day_commissions` Decimal(38, 12),
                    `current_month_commissions` Decimal(38, 12),
                    `current_day_agent_commissions` Decimal(38, 12),
                    `current_month_agent_commissions` Decimal(38, 12),
                    `current_day_fees` Decimal(38, 12),
                    `current_month_fees` Decimal(38, 12),
                    `accumulated_daily_commissions` Decimal(38, 12),
                    `accumulated_monthly_commissions` Decimal(38, 12),
                    `accumulated_daily_fees` Decimal(38, 12),
                    `accumulated_monthly_fees` Decimal(38, 12),
                    `current_day_closed_positions_swaps` Decimal(38, 12),
                    `current_month_closed_positions_swaps` Decimal(38, 12),
                    `current_day_interest` Decimal(38, 12),
                    `current_month_interest` Decimal(38, 12),
                    `current_day_deposits` Decimal(38, 12),
                    `current_month_deposits` Decimal(38, 12),
                    `current_day_withdrawals` Decimal(38, 12),
                    `current_month_withdrawals` Decimal(38, 12),
                    `current_day_credit_transactions` Decimal(38, 12),
                    `current_month_credit_transactions` Decimal(38, 12),
                    `current_day_adjustment_transactions` Decimal(38, 12),
                    `current_month_adjustment_transactions` Decimal(38, 12),
                    `current_day_balance_bonus_transactions` Decimal(38, 12),
                    `current_month_balance_bonus_transactions` Decimal(38, 12),
                    `current_day_credit_bonus_transactions` Decimal(38, 12),
                    `current_month_credit_bonus_transactions` Decimal(38, 12),
                    `current_day_dividends` Decimal(38, 12),
                    `current_month_dividends` Decimal(38, 12),
                    `current_day_tax` Decimal(38, 12),
                    `current_month_tax` Decimal(38, 12),
                    `current_day_stopout_compensations` Decimal(38, 12),
                    `current_month_stopout_compensations` Decimal(38, 12),
                    `current_day_stopout_credit_adjustments` Decimal(38, 12),
                    `current_month_stopout_credit_adjustments` Decimal(38, 12),
                    `current_day_volume_in_usd` Decimal(38, 12),
                    `current_month_volume_in_usd` Decimal(38, 12),
                    `current_day_volume_in_account_currency` Decimal(38, 12),
                    `current_month_volume_in_account_currency` Decimal(38, 12),
                    `marker_sequence` UInt64 MATERIALIZED if(trading_account_id = 0, sequence, 0)
                )
                ENGINE = ReplacingMergeTree(sequence)
                PARTITION BY sender_id
                ORDER BY (sender_id, trading_account_id, marker_sequence)
                TTL toDateTime(batch_time) + toIntervalDay({deleteTtlDays}) WHERE event_type = 'Delete'
                SETTINGS index_granularity = 8192, min_age_to_force_merge_seconds = {forceMergeSeconds}
                """.ReplaceLineEndings("\n");
    }

    protected static List<string> AccountStatesJournalEntries(string connectionString)
    {
        return Strings(connectionString,
            "SELECT script_name FROM schema_versions " +
            "WHERE script_name LIKE '%create_account_state_events%' OR script_name LIKE '%account_states%.sql::%' OR script_name LIKE '%account_state_events_ttl.sql::%' " +
            "ORDER BY script_name");
    }
}
