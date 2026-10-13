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
/// What is particular to the group tables: the agreed columns, exact decimals, the one nullable column, a name two groups
/// can share, and trades read beside their group.
/// </summary>
/// <remarks>
/// What the four logs share runs against this one in the EventLog suites.
/// </remarks>
[Trait("Category", "Integration")]
public class GroupsIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    private const string GroupName = "real/FPlive/CHF_IN_NT_R_H";

    /// <summary>
    /// The agreed schema written out: the only test that fails if group_events is reordered or retyped.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the agreed columns and types in the agreed order")]
    public void Schema_Should_carry_the_agreed_columns_and_types_in_the_agreed_order()
    {
        // arrange, act
        Migrate(EventLog.Groups);

        // assert
        Assert.Equal(
        [
            "id bigint", "time_inserted timestamp with time zone", "sender_id bigint", "event_type smallint",
            "group_id bigint", "version bigint", "name character varying(128)", "company character varying(64)",
            "email_service_id bigint null", "currency character varying(16)", "currency_decimal_places integer",
            "interest_rate numeric", "free_margin_mode integer", "stopout_value_type integer", "stopout numeric",
            "margin_call_value_type integer", "margin_call numeric", "open_position_type integer",
            "exchange_trading_mode smallint", "default_leverage integer", "demo_deposit numeric",
            "demo_inactivity_period integer", "max_trade_history_period integer", "max_orders integer",
            "max_positions integer", "max_market_data_subscriptions integer", "send_daily_reports boolean",
            "send_monthly_reports boolean", "symbol_group_profile_count bigint", "commission_profile_count bigint"
        ], ColumnDefinitions(Log.Events));
    }

    /// <summary>
    /// A float anywhere on the way would round the deposit and the rate below.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep thresholds, deposits and rates as exact decimals")]
    public void Edge_case_Should_keep_thresholds_deposits_and_rates_as_exact_decimals()
    {
        // arrange
        Migrate(EventLog.Groups);

        // act, the API's group 7 sample, with a deposit and a rate beyond the precision of a double
        Export(Group(7, GroupName)
            .With("company", "FP Trading LLC")
            .With("currency", "CHF")
            .With("stopout", "50")
            .With("margin_call", "100")
            .With("demo_deposit", "10000.000000000000000001")
            .With("interest_rate", "-1.123456789012345678"));

        // assert
        Assert.Equal(["50", "100", "10000.000000000000000001", "-1.123456789012345678"],
            Values(7, "stopout", "margin_call", "demo_deposit", "interest_rate"));
    }

    /// <summary>
    /// The one nullable column: NOT NULL would stall the export, and a COALESCE would keep a service that was removed.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep a group without an email service as NULL")]
    public void Edge_case_Should_keep_a_group_without_an_email_service_as_NULL()
    {
        // arrange
        Migrate(EventLog.Groups);

        // act, one group created without an email service and one with
        Export(Group(25, "no emails"), Group(26, "emails").With("email_service_id", 1L));

        // assert
        Assert.Equal(["25: NULL", "26: 1"], EmailServices());

        // and a change that removes the service clears it
        Export(Group(26, "emails", version: 2));

        Assert.Equal(["25: NULL", "26: NULL"], EmailServices());
    }

    /// <summary>
    /// Why name is not unique: a unique index would refuse this re-announcement and stall the export.
    /// </summary>
    [Fact(DisplayName = "Edge case Should hold two groups under one name while a resync is converging")]
    public void Edge_case_Should_hold_two_groups_under_one_name_while_a_resync_is_converging()
    {
        // arrange
        Migrate(EventLog.Groups);
        Export(Group(5, GroupName, version: 3));

        // act, 5 was deleted while the sender was down and its name created again as 9
        Export(ResyncStarted());
        Export(Group(9, GroupName));

        // assert, both hold the name until the end marker sweeps the old one
        Assert.Equal(["5", "9"], GroupIdsNamed(GroupName));

        Export(ResyncFinished());

        Assert.Equal(["9"], GroupIdsNamed(GroupName));
    }

    /// <summary>
    /// Each sender numbers its own groups. The wrong join is asserted too, so the fixture keeps reproducing it.
    /// </summary>
    [Fact(DisplayName = "Reading Should need sender_id as well as group_id to join trades to their group")]
    public void Reading_Should_need_sender_id_as_well_as_group_id_to_join_trades_to_their_group()
    {
        // arrange, group 25 is a real group on one sender and a demo one on the other, with trades in each
        Migrate(EventLog.Groups);
        Export(Group(25, GroupName), Group(25, "demo/USD", sender: OtherSender));

        Trade(1, groupId: 25, GroupName);
        Trade(2, groupId: 25, GroupName);
        Trade(1, groupId: 25, "demo/USD", sender: OtherSender);

        // act, the full key against the id alone
        const string fullKey = "FROM trades t JOIN groups g ON g.sender_id = t.sender_id AND g.group_id = t.group_id";
        const string idOnly = "FROM trades t JOIN groups g ON g.group_id = t.group_id";

        // assert, three trades have a group, and the id alone invents three more, each in the wrong group
        Assert.Equal(3, Scalar<long>($"SELECT count(*) {fullKey}"));
        Assert.Equal(0, Scalar<long>($"SELECT count(*) {fullKey} WHERE g.name <> t.group_name"));

        Assert.Equal(6, Scalar<long>($"SELECT count(*) {idOnly}"));
        Assert.Equal(3, Scalar<long>($"SELECT count(*) {idOnly} WHERE g.name <> t.group_name"));
    }

    private LogEvent Group(long id, string name, long version = 1, long sender = Sender)
    {
        return State(id, version, sender: sender).With("name", name);
    }

    private void Trade(long tradeId, long groupId, string groupName, long sender = Sender)
    {
        InsertRow("trades",
            ("sender_id", $"{sender}"),
            ("trade_id", $"{tradeId}"),
            ("group_id", $"{groupId}"),
            ("group_name", $"'{groupName}'"));
    }

    /// <summary>
    /// Each group's email service, as <c>group_id: email_service_id</c>, with NULL spelt out.
    /// </summary>
    private List<string> EmailServices()
    {
        return Strings("SELECT group_id || ': ' || COALESCE(email_service_id::text, 'NULL') FROM groups ORDER BY group_id");
    }

    private List<string> GroupIdsNamed(string name)
    {
        return Strings($"SELECT group_id::text FROM groups WHERE name = '{name}' ORDER BY group_id");
    }
}
