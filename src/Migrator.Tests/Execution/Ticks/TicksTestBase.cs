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

using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using DbUp.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Extensions;
using YB.TradeServer.Db.Migrator.Handlers;

namespace YB.TradeServer.Db.Migrator.Tests.Execution.Ticks;

/// <summary>
/// The vocabulary the ticks suites are written in, over a throwaway ClickHouse database per test.
/// </summary>
/// <remarks>
/// Pins ClickHouse's behaviour under our DDL, as verified on 26.7. Each test gets its own database, users and roles.
/// </remarks>
public abstract class TicksTestBase(ConfigBuilder configBuilder, ITestOutputHelper output) : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    protected const ulong Sender = 7;
    protected const ulong OtherSender = 8;

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

    protected const int UnknownTable = 60;
    protected const int ArgumentOutOfBound = 69;
    protected const int IllegalFinal = 181;
    protected const int IndexNotUsed = 277;
    protected const int DecimalOverflow = 407;
    protected const int AccessDenied = 497;
    protected const int AuthenticationFailed = 516;

    private readonly List<(string ConnectionString, string DatabaseName)> provisionedDatabases = [];

    /// <summary>
    /// The instant a test's ticks are stamped around: an hour back, in whole seconds, well inside the retention.
    /// </summary>
    private readonly DateTime recent = DateTime.UnixEpoch.AddSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddHours(-1);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (connectionString, databaseName) in provisionedDatabases)
        {
            try
            {
                await using var connection = new ClickHouseConnection(WithDatabase(connectionString, "system"));
                await connection.OpenAsync();

                // users and roles are server wide, so dropping the database would leave them behind
                string[] statements =
                [
                    $"DROP DATABASE IF EXISTS {databaseName}",
                    $"DROP USER IF EXISTS {databaseName}_exporter, {databaseName}_reader",
                    $"DROP ROLE IF EXISTS role_{databaseName}_exporter, role_{databaseName}_ro"
                ];

                foreach (var statement in statements)
                {
                    await using var command = connection.CreateCommand(statement);
                    await command.ExecuteNonQueryAsync();
                }
            }
            catch (Exception e)
            {
                output.WriteLine($"Unable to drop {databaseName}: {e.Message}");
            }
        }
    }

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

    protected DbUpRunner CreateRunner()
    {
        return new(NullLogger<DbUpRunner>.Instance, new TestDbUpLogProvider(output));
    }

    protected DbMigratorRequest CreateRequest(int? ticksTtlDays = null)
    {
        var configured = configBuilder.ClickHouseConnectionString;

        Assert.False(string.IsNullOrWhiteSpace(configured), "No ClickHouse connection string configured. Start docker-compose.yml or set ClickHouse__ConnectionString.");

        var databaseName = $"ticks_integration_{Guid.NewGuid():N}"[..40];

        provisionedDatabases.Add((configured!, databaseName));

        return new(
            DatabaseProvider.ClickHouse,
            WithDatabase(configured!, databaseName),
            databaseName,
            "schema_versions",
            [new(true, "clickhouse")],
            configBuilder.RepositoryRoot,
            Variables(databaseName, ticksTtlDays),
            false,
            false,
            null,
            [
                new() { Access = "exporter", User = $"{databaseName}_exporter", Password = "passw0rd" },
                new() { Access = "ro", User = $"{databaseName}_reader", Password = "passw0rd" }
            ]);
    }

    /// <summary>
    /// The CLI's own variables, so new ones reach these tests automatically; a retention goes in as --variables.
    /// </summary>
    protected static Dictionary<string, string> Variables(string databaseName, int? ticksTtlDays)
    {
        return MigrationHandler.BuildVariables(DatabaseProvider.ClickHouse, databaseName, null,
            new() { Variables = ticksTtlDays is null ? null : [$"ticks_ttl_days={ticksTtlDays}"] });
    }

    protected static DbMigratorRequest WithRetention(DbMigratorRequest request, int days)
    {
        return request with { Variables = Variables(request.DatabaseName, days) };
    }

    protected static void AssertSuccessful(IEnumerable<(string SubFolder, DatabaseUpgradeResult Result)> results)
    {
        foreach (var (subFolder, result) in results)
        {
            Assert.True(result.Successful, $"{subFolder} failed on '{result.ErrorScript?.Name}': {result.Error}");
        }
    }

    protected static void CreateDatabase(DbMigratorRequest request)
    {
        Execute(WithDatabase(request.ConnectionString, "system"), $"CREATE DATABASE {request.DatabaseName}");
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

    protected DateTime At(double seconds)
    {
        return recent.AddSeconds(seconds);
    }

    protected static DateTime DaysAgo(int days)
    {
        return DateTime.UtcNow.AddDays(-days);
    }

    protected static DateTime HoursAgo(int hours)
    {
        return DateTime.UtcNow.AddHours(-hours);
    }

    protected static DateTime Utc(string time)
    {
        return DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
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

    /// <summary>
    /// A time as ClickHouse prints a DateTime64(6).
    /// </summary>
    protected static string Format(DateTime time)
    {
        return time.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    protected static string Time(DateTime time)
    {
        return $"'{Format(time)}'";
    }

    private static string Quote(string value)
    {
        return $"'{SqlSafety.EscapeLiteral(DatabaseProvider.ClickHouse, value)}'";
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

    protected static long Count(string connectionString, string from)
    {
        return Scalar<long>(connectionString, $"SELECT count() FROM {from}");
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

    /// <summary>
    /// SHOW CREATE TABLE without the database name, which differs per test.
    /// </summary>
    protected static string ShowCreate(string connectionString, string table)
    {
        var database = new ClickHouseConnectionStringBuilder(connectionString).Database;

        return Scalar<string>(connectionString, $"SHOW CREATE TABLE {table}").Replace($"CREATE TABLE {database}.", "CREATE TABLE ");
    }

    protected static List<string> Tables(string connectionString)
    {
        return Strings(connectionString, "SELECT name FROM system.tables WHERE database = currentDatabase() ORDER BY name");
    }

    protected static List<string> GrantedRoles(string connectionString, string userName)
    {
        return Strings(connectionString, $"SELECT granted_role_name FROM system.role_grants WHERE user_name = '{userName}' ORDER BY granted_role_name");
    }

    protected static List<string> RoleGrants(string connectionString, string roleName)
    {
        return Strings(connectionString, $"SELECT concat(access_type, ' ON ', table) FROM system.grants WHERE role_name = '{roleName}' ORDER BY table");
    }

    protected static List<string> TicksJournalEntries(string connectionString)
    {
        return Strings(connectionString,
            "SELECT script_name FROM schema_versions WHERE script_name LIKE '%create_ticks.sql' OR script_name LIKE '%ticks_ttl.sql::%' ORDER BY script_name");
    }

    // ---------------------------------------------------------------------------------------------------
    // Accounts
    // ---------------------------------------------------------------------------------------------------

    protected static string ExporterConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "exporter"));
    }

    protected static string ReaderConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "ro"));
    }

    protected static string WithUser(string connectionString, DatabaseUser user)
    {
        return new ClickHouseConnectionStringBuilder(connectionString) { Username = user.User, Password = user.Password }.ToString();
    }

    protected static string WithDatabase(string connectionString, string databaseName)
    {
        return new ClickHouseConnectionStringBuilder(connectionString) { Database = databaseName }.ToString();
    }

    // ---------------------------------------------------------------------------------------------------
    // SQL
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the statement and asserts the server refused it with the given code, naming the statement if not.
    /// </summary>
    protected static void AssertError(string connectionString, string sql, int errorCode)
    {
        var exception = Record.Exception(() => Execute(connectionString, sql));

        Assert.True(exception is ClickHouseServerException server && server.ErrorCode == errorCode,
            $"Expected error {errorCode} from: {sql}{Environment.NewLine}Got: {exception?.Message ?? "no error"}");
    }

    protected static void AssertDenied(string connectionString, string sql)
    {
        AssertError(connectionString, sql, AccessDenied);
    }

    protected static void Execute(string connectionString, string sql)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    protected static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand(sql);

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    protected static List<string> Strings(string connectionString, string sql)
    {
        return Column(connectionString, sql, value => Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    protected static List<long> Longs(string connectionString, string sql)
    {
        return Column(connectionString, sql, value => Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static List<T> Column<T>(string connectionString, string sql, Func<object, T> convert)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand(sql);
        using var reader = command.ExecuteReader();

        var values = new List<T>();

        while (reader.Read())
        {
            values.Add(convert(reader.GetValue(0)));
        }

        return values;
    }
}
