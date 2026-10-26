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

namespace YB.TradeServer.Db.Migrator.Tests.Execution.Positions;

/// <summary>
/// The vocabulary the positions suites are written in, over a throwaway ClickHouse database per test.
/// </summary>
/// <remarks>
/// Pins ClickHouse's behaviour under our DDL. Each test gets its own database, users and roles.
/// </remarks>
public abstract class PositionsTestBase(ConfigBuilder configBuilder, ITestOutputHelper output) : ClickHouseTestBase(configBuilder, output)
{
    /// <summary>
    /// Every column the exporter writes, in the table's order; marker_sequence is materialized by the server.
    /// </summary>
    protected const string PositionColumns =
        "sender_id, sequence, batch_time, event_type, " +
        "position_id, trading_account_id, group_id, group_name, symbol_id, symbol_name, " +
        "side, volume, lot_size, avg_open_price, " +
        "stop_loss, stop_loss_order_id, take_profit, take_profit_order_id, " +
        "close_market_price, close_group_markup, pl, margin, asset_or_liability, profit_conversion_rate, margin_conversion_rate, " +
        "swaps, commission, fee, " +
        "price_decimal_places, account_currency_decimal_places, " +
        "created, created_by_order_id, updated, updated_by_order_id";

    /// <summary>
    /// The refresh period of a test that must not see a scheduled refresh: an hour, far longer than any test.
    /// </summary>
    protected const int NoScheduledRefresh = 3600;

    /// <summary>
    /// The batch_time a row gets when the test does not care: an hour back, well inside the retention.
    /// </summary>
    private static readonly DateTime defaultBatchTime = DateTime.UnixEpoch.AddSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddHours(-1);

    // ---------------------------------------------------------------------------------------------------
    // Migrating
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Provisions and migrates a database; returns its connection string.
    /// </summary>
    /// <param name="variables">--variables entries, such as <c>positions_delete_ttl_days=7</c>.</param>
    protected string Migrate(params string[] variables)
    {
        var request = CreateRequest(variables);

        AssertSuccessful(CreateRunner().Run(request));

        return request.ConnectionString;
    }

    protected DbMigratorRequest CreateRequest(params string[] variables)
    {
        return CreateClickHouseRequest("positions_integration_", variables);
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
        ulong PositionId = 1,
        ulong SenderId = Sender,
        string Volume = "1",
        DateTime? BatchTime = null)
    {
        public string ToSql()
        {
            var batchTime = Time(BatchTime ?? defaultBatchTime);

            return $"({SenderId}, {Sequence}, {batchTime}, {Quote(EventType)}, " +
                   $"{PositionId}, 100, 10, 'Group A', 1, 'EURUSD', " +
                   $"'buy', {Volume}, 100000, 1.0855, " +
                   "0, 0, 0, 0, " +
                   "1.0856, 0, 0, 0, 0, 1, 1, " +
                   "0, 0, 0, " +
                   "5, 2, " +
                   $"{batchTime}, 0, {batchTime}, 0)";
        }
    }

    protected static Row Upsert(ulong sequence, ulong positionId, string volume = "1", ulong senderId = Sender)
    {
        return new(sequence, "Upsert", positionId, senderId, volume);
    }

    protected static Row Delete(ulong sequence, ulong positionId, ulong senderId = Sender)
    {
        return new(sequence, "Delete", positionId, senderId);
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
    /// A complete snapshot from <paramref name="start"/> on: the start marker, one Upsert per position, the end marker.
    /// </summary>
    protected static Row[] Snapshot(ulong start, ulong senderId, params ulong[] positionIds)
    {
        return
        [
            SnapshotStart(start, senderId),
            .. positionIds.Select((positionId, index) => Upsert(start + 1 + (ulong)index, positionId, senderId: senderId)),
            SnapshotEnd(start + 1 + (ulong)positionIds.Length, senderId)
        ];
    }

    protected static string InsertInto(string table, params Row[] rows)
    {
        return $"INSERT INTO {table} ({PositionColumns}) VALUES {string.Join(", ", rows.Select(row => row.ToSql()))}";
    }

    protected static string InsertSql(params Row[] rows)
    {
        return InsertInto("positions", rows);
    }

    /// <summary>
    /// One statement, one batch, the shape the exporter sends.
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
        Execute(connectionString, "ALTER TABLE positions MATERIALIZE TTL SETTINGS mutations_sync = 2");
    }

    /// <summary>
    /// Refreshes the view now rather than on its schedule, and waits for the refresh to finish.
    /// </summary>
    protected static void Refresh(string connectionString, string view = "positions_state")
    {
        Execute(connectionString, $"SYSTEM REFRESH VIEW {view}");
        Execute(connectionString, $"SYSTEM WAIT VIEW {view}");
    }

    // ---------------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The view as text, one "sender/position: volume" per open position.
    /// </summary>
    protected static List<string> State(string connectionString, string view = "positions_state")
    {
        return Strings(connectionString,
            $"SELECT concat(toString(sender_id), '/', toString(position_id), ': ', toString(volume)) FROM {view} ORDER BY sender_id, position_id");
    }

    /// <summary>
    /// Refreshes positions_state and asserts both views show the expected open positions: the stored state after a
    /// refresh is the live one.
    /// </summary>
    protected static void AssertState(string connectionString, IEnumerable<string> expected)
    {
        Refresh(connectionString);

        var positions = expected.ToList();

        Assert.Equal(positions, State(connectionString, "positions_state_live"));
        Assert.Equal(positions, State(connectionString));
    }

    /// <summary>
    /// The sequences of every row on disk, merged or not.
    /// </summary>
    protected static List<long> Sequences(string connectionString)
    {
        return Longs(connectionString, "SELECT sequence FROM positions ORDER BY sender_id, sequence");
    }

    // ---------------------------------------------------------------------------------------------------
    // The catalog
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The table the migration leaves, as SHOW CREATE TABLE prints it: the DDL in ClickHouse's canonical form.
    /// </summary>
    protected static string AgreedTable(string table = "positions", int deleteTtlDays = 30)
    {
        // the raw string takes this file's line endings, which a Windows checkout makes CRLF
        return $"""
                CREATE TABLE {table}
                (
                    `sender_id` UInt64,
                    `sequence` UInt64,
                    `batch_time` DateTime64(6, 'UTC'),
                    `event_type` Enum8('Upsert' = 0, 'Delete' = 1, 'SnapshotStart' = 2, 'SnapshotEnd' = 3),
                    `position_id` UInt64,
                    `trading_account_id` UInt64,
                    `group_id` UInt64,
                    `group_name` LowCardinality(String),
                    `symbol_id` UInt64,
                    `symbol_name` LowCardinality(String),
                    `side` Enum8('buy' = 0, 'sell' = 1),
                    `volume` Decimal(18, 8),
                    `lot_size` Float64,
                    `avg_open_price` Decimal(18, 8),
                    `stop_loss` Decimal(18, 8),
                    `stop_loss_order_id` UInt64,
                    `take_profit` Decimal(18, 8),
                    `take_profit_order_id` UInt64,
                    `close_market_price` Decimal(18, 8),
                    `close_group_markup` Int32,
                    `pl` Decimal(38, 8),
                    `margin` Decimal(38, 8),
                    `asset_or_liability` Decimal(38, 8),
                    `profit_conversion_rate` Float64,
                    `margin_conversion_rate` Float64,
                    `swaps` Decimal(38, 8),
                    `commission` Decimal(38, 8),
                    `fee` Decimal(38, 8),
                    `price_decimal_places` UInt8,
                    `account_currency_decimal_places` UInt8,
                    `created` DateTime64(6, 'UTC'),
                    `created_by_order_id` UInt64,
                    `updated` DateTime64(6, 'UTC'),
                    `updated_by_order_id` UInt64,
                    `marker_sequence` UInt64 MATERIALIZED if(position_id = 0, sequence, 0)
                )
                ENGINE = ReplacingMergeTree(sequence)
                PARTITION BY sender_id
                ORDER BY (sender_id, position_id, marker_sequence)
                TTL toDateTime(batch_time) + toIntervalDay({deleteTtlDays}) WHERE event_type = 'Delete'
                SETTINGS min_age_to_force_merge_seconds = 600, index_granularity = 8192
                """.ReplaceLineEndings("\n");
    }

    protected static List<string> PositionsJournalEntries(string connectionString)
    {
        return Strings(connectionString,
            "SELECT script_name FROM schema_versions " +
            "WHERE script_name LIKE '%create_positions%' OR script_name LIKE '%positions_state%.sql::%' OR script_name LIKE '%positions_ttl.sql::%' " +
            "ORDER BY script_name");
    }
}
