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

namespace YB.TradeServer.Db.Migrator.Tests.Execution.Ticks;

/// <summary>
/// The vocabulary the ticks suites are written in, over a throwaway ClickHouse database per test.
/// </summary>
/// <remarks>
/// Pins ClickHouse's behaviour under our DDL, as verified on 26.7. Each test gets its own database, users and roles.
/// </remarks>
public abstract class TicksTestBase(ConfigBuilder configBuilder, ITestOutputHelper output) : ClickHouseTestBase(configBuilder, output)
{
    protected const string TickColumns =
        "sender_id, sequence, batch_time, created_time, symbol_id, short_symbol_name, bid_price, bid_volume, ask_price, ask_volume";

    /// <summary>
    /// A tick's book as text: bid price, bid volume, ask price, ask volume.
    /// </summary>
    protected const string BookColumns =
        "concatWithSeparator(' ', toString(bid_price), toString(bid_volume), toString(ask_price), toString(ask_volume))";

    /// <summary>
    /// Keeps the fixed 2026 dates live, well short of the ~29,000 days at which the TTL expression overflows DateTime.
    /// </summary>
    protected const int LongRetentionDays = 20000;

    // ---------------------------------------------------------------------------------------------------
    // Migrating
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Provisions and migrates a database; returns its connection string.
    /// </summary>
    protected string Migrate(int? ticksTtlDays = null)
    {
        var request = CreateRequest(ticksTtlDays);

        AssertSuccessful(CreateRunner().Run(request));

        return request.ConnectionString;
    }

    protected DbMigratorRequest CreateRequest(int? ticksTtlDays = null)
    {
        return CreateClickHouseRequest("ticks_integration_", RetentionVariables(ticksTtlDays));
    }

    /// <summary>
    /// The CLI's own variables, so new ones reach these tests automatically; a retention goes in as --variables.
    /// </summary>
    protected static Dictionary<string, string> Variables(string databaseName, int? ticksTtlDays)
    {
        return ClickHouseVariables(databaseName, RetentionVariables(ticksTtlDays));
    }

    protected static DbMigratorRequest WithRetention(DbMigratorRequest request, int days)
    {
        return request with { Variables = Variables(request.DatabaseName, days) };
    }

    private static string[] RetentionVariables(int? ticksTtlDays)
    {
        return ticksTtlDays is null ? [] : [$"ticks_ttl_days={ticksTtlDays}"];
    }

    /// <summary>
    /// The ticks table as an operator would have created it by hand from the guide, on the given engine.
    /// </summary>
    protected static string HandMadeTicksTable(string engine)
    {
        return $"""
                CREATE TABLE ticks
                (
                    sender_id UInt64, sequence UInt64,
                    batch_time DateTime64(6, 'UTC') CODEC(Delta, Default), created_time DateTime64(6, 'UTC') CODEC(Delta, Default),
                    symbol_id UInt64, short_symbol_name LowCardinality(String),
                    bid_price Decimal128(12), bid_volume Decimal64(8), ask_price Decimal128(12), ask_volume Decimal64(8)
                )
                ENGINE = {engine}
                PARTITION BY toYYYYMM(created_time)
                PRIMARY KEY (sender_id, short_symbol_name)
                ORDER BY (sender_id, short_symbol_name, created_time, sequence)
                """;
    }

    // ---------------------------------------------------------------------------------------------------
    // Writing ticks, as the exporter does
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// One tick. Prices and volumes are SQL literals, so no value is rounded on its way to the server.
    /// </summary>
    protected sealed record Tick(
        ulong Sequence,
        DateTime CreatedTime,
        string Symbol = "EURUSD",
        ulong SenderId = Sender,
        ulong SymbolId = 1,
        string BidPrice = "1.0855",
        string BidVolume = "1000000",
        string AskPrice = "1.0856",
        string AskVolume = "500000",
        DateTime? BatchTime = null)
    {
        public string ToSql()
        {
            return $"({SenderId}, {Sequence}, {Time(BatchTime ?? CreatedTime)}, {Time(CreatedTime)}, {SymbolId}, {Quote(Symbol)}, " +
                   $"{BidPrice}, {BidVolume}, {AskPrice}, {AskVolume})";
        }
    }

    /// <summary>
    /// A cut-off on the 15th of last month, and the retention in days that puts it there.
    /// </summary>
    protected static (DateTime CutOff, int Days) CutOffMidLastMonth()
    {
        var now = DateTime.UtcNow;
        var lastMonth = now.AddMonths(-1);
        var cutOff = new DateTime(lastMonth.Year, lastMonth.Month, 15, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);

        return (cutOff, (now.Date - cutOff.Date).Days);
    }

    protected static string Values(params Tick[] ticks)
    {
        return string.Join(", ", ticks.Select(tick => tick.ToSql()));
    }

    protected static string InsertInto(string table, params Tick[] ticks)
    {
        return $"INSERT INTO {table} ({TickColumns}) VALUES {Values(ticks)}";
    }

    protected static string InsertSql(params Tick[] ticks)
    {
        return InsertInto("ticks", ticks);
    }

    /// <summary>
    /// One statement, one batch, the shape the exporter sends.
    /// </summary>
    protected static void Insert(string connectionString, params Tick[] ticks)
    {
        Execute(connectionString, InsertSql(ticks));
    }

    /// <summary>
    /// Runs the TTL pass now rather than on its four-hourly schedule (merge_with_ttl_timeout).
    /// </summary>
    protected static void ApplyRetention(string connectionString)
    {
        Execute(connectionString, "ALTER TABLE ticks MATERIALIZE TTL SETTINGS mutations_sync = 2");
    }

    // ---------------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------------

    protected static List<string> Books(string connectionString)
    {
        return Strings(connectionString, $"SELECT {BookColumns} FROM ticks ORDER BY sequence");
    }

    protected static List<long> Sequences(string connectionString)
    {
        return Longs(connectionString, "SELECT sequence FROM ticks ORDER BY sequence");
    }

    // ---------------------------------------------------------------------------------------------------
    // The catalog
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The table the migration leaves, as SHOW CREATE TABLE prints it: the guide's DDL in ClickHouse's canonical form.
    /// </summary>
    protected static string AgreedTable(string table = "ticks")
    {
        // the raw string takes this file's line endings, which a Windows checkout makes CRLF
        return $"""
                CREATE TABLE {table}
                (
                    `sender_id` UInt64,
                    `sequence` UInt64,
                    `batch_time` DateTime64(6, 'UTC') CODEC(Delta(8), Default),
                    `created_time` DateTime64(6, 'UTC') CODEC(Delta(8), Default),
                    `symbol_id` UInt64,
                    `short_symbol_name` LowCardinality(String),
                    `bid_price` Decimal(38, 12),
                    `bid_volume` Decimal(18, 8),
                    `ask_price` Decimal(38, 12),
                    `ask_volume` Decimal(18, 8)
                )
                ENGINE = ReplacingMergeTree
                PARTITION BY toYYYYMM(created_time)
                PRIMARY KEY (sender_id, short_symbol_name)
                ORDER BY (sender_id, short_symbol_name, created_time, sequence)
                TTL toDateTime(created_time) + toIntervalDay(31)
                SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1
                """.ReplaceLineEndings("\n");
    }

    protected static List<string> TicksJournalEntries(string connectionString)
    {
        return Strings(connectionString,
            "SELECT script_name FROM schema_versions WHERE script_name LIKE '%create_ticks.sql' OR script_name LIKE '%ticks_ttl.sql::%' ORDER BY script_name");
    }
}
