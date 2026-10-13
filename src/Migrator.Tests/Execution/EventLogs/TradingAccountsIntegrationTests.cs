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

namespace YB.TradeServer.Db.Migrator.Tests.Execution.EventLogs;

/// <summary>
/// What is particular to the trading account tables: their columns and their times.
/// </summary>
/// <remarks>
/// What the four logs share runs against this one in the EventLog suites.
/// </remarks>
[Trait("Category", "Integration")]
public class TradingAccountsIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    /// <summary>
    /// The columns written out: the only test that fails if the log is reordered or retyped.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the log columns and types in their declared order")]
    public void Schema_Should_carry_the_log_columns_and_types_in_their_declared_order()
    {
        // arrange, act
        Migrate(EventLog.TradingAccounts);

        // assert
        Assert.Equal(
        [
            "id bigint", "time_inserted timestamp with time zone", "sender_id bigint", "event_type smallint",
            "trading_account_id bigint", "version bigint", "group_id bigint", "client_id bigint", "introducer bigint",
            "time_created timestamp with time zone", "time_modified timestamp with time zone",
            "time_password_last_changed timestamp with time zone", "leverage integer", "enabled boolean", "allow_trading boolean",
            "external_account_id character varying(64)", "comment character varying(256)"
        ], ColumnDefinitions(Log.Events));
    }

    /// <summary>
    /// PostgreSQL rounds a seventh fractional digit where ClickHouse cuts it, so a time can roll into the next day.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep times to the microsecond and round a seventh digit, whatever the session time zone")]
    public void Edge_case_Should_keep_times_to_the_microsecond_and_round_a_seventh_digit_whatever_the_session_time_zone()
    {
        // arrange
        Migrate(EventLog.TradingAccounts);

        // act, from a Tokyo session, each time with the offset the exporter sends
        ExportFromTimeZone("Asia/Tokyo", State(1)
            .With("time_created", "2026-01-31 23:59:59.999999+00")
            .With("time_modified", "2026-01-31 23:59:59.9999995+00")
            .With("time_password_last_changed", "2026-01-31 23:59:59.9999994+00"));

        // assert, read back in UTC
        Assert.Equal(["2026-01-31 23:59:59.999999", "2026-02-01 00:00:00.000000", "2026-01-31 23:59:59.999999"],
            Values(1, InUtc("time_created"), InUtc("time_modified"), InUtc("time_password_last_changed")));
    }

    /// <summary>
    /// Without an offset a time is read in the zone of the session that sent it, so the exporter has to send one.
    /// </summary>
    [Fact(DisplayName = "Edge case Should read a time sent without an offset in the time zone of the session that sent it")]
    public void Edge_case_Should_read_a_time_sent_without_an_offset_in_the_time_zone_of_the_session_that_sent_it()
    {
        // arrange
        Migrate(EventLog.TradingAccounts);

        // act, the same wall clock time from a Tokyo session and from a UTC one
        ExportFromTimeZone("Asia/Tokyo", State(1).With("time_created", "2026-01-31 23:59:59"));
        ExportFromTimeZone("UTC", State(2).With("time_created", "2026-01-31 23:59:59"));

        // assert, nine hours apart
        Assert.Equal("2026-01-31 14:59:59.000000", Value(1, InUtc("time_created")));
        Assert.Equal("2026-01-31 23:59:59.000000", Value(2, InUtc("time_created")));
    }

    private void ExportFromTimeZone(string timeZone, params LogEvent[] batch)
    {
        using var exporter = Open(Exporter);

        Execute(exporter, $"SET TimeZone = '{timeZone}'");
        Copy(exporter, batch);
    }

    private static string InUtc(string column)
    {
        return $"to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US')";
    }
}
