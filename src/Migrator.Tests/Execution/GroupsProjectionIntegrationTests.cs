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

using DbUp.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;
using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Handlers;

namespace YB.TradeServer.Db.Migrator.Tests.Execution;

/// <summary>
/// The trigger that maintains the groups table from the group event log.
/// </summary>
/// <remarks>
/// Mirrors <see cref="TradingAccountsProjectionIntegrationTests"/>. Rows are built from the live schema, so no
/// column list is kept by hand. Each test gets its own database.
/// </remarks>
[Trait("Category", "Integration")]
public class GroupsProjectionIntegrationTests : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    private const long Sender = 7;
    private const long OtherSender = 8;

    /// <summary>
    /// Boolean <c>b</c> carries bit <c>p</c> of <c>b + 1</c> in event <c>p</c>, so any two differ in some event.
    /// </summary>
    private const int BooleanPatterns = 5;

    private readonly ConfigBuilder configBuilder;
    private readonly ITestOutputHelper output;
    private readonly List<(string ConnectionString, string DatabaseName)> provisionedDatabases = [];

    public GroupsProjectionIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output)
    {
        this.configBuilder = configBuilder;
        this.output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (connectionString, databaseName) in provisionedDatabases)
        {
            try
            {
                await using var connection = new NpgsqlConnection(WithDatabase(connectionString, "postgres"));
                await connection.OpenAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = $@"DROP DATABASE IF EXISTS ""{databaseName}"" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception e)
            {
                output.WriteLine($"Unable to drop {databaseName}: {e.Message}");
            }
        }
    }

    [Fact(DisplayName = "Schema Should create every object the projection needs")]
    public void Schema_Should_create_every_object_the_projection_needs()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.True(TableExists(connectionString, "group_events"));
        Assert.True(TableExists(connectionString, "groups"));
        Assert.True(TableExists(connectionString, "groups_resync"));

        Assert.True(IndexExists(connectionString, "idx_group_row_group_events"));
        Assert.True(IndexExists(connectionString, "idx_markers_group_events"));

        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_project_groups' AND NOT tgisinternal"));
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_proc WHERE proname = 'project_groups'"));
    }

    /// <summary>
    /// A type or nullability drift would not fail the migration; it would round, truncate or stall the export.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the same group columns in the log and the state table")]
    public void Schema_Should_carry_the_same_group_columns_in_the_log_and_the_state_table()
    {
        // arrange
        var connectionString = Migrate();

        // act, format_type and attnotnull so width and nullability drifts show
        const string sql = """
                           WITH cols AS (SELECT c.relname                                           AS table_name,
                                                a.attname                                           AS column_name,
                                                format_type(a.atttypid, a.atttypmod) ||
                                                CASE WHEN a.attnotnull THEN ' not null' ELSE '' END AS column_type
                                         FROM pg_attribute a
                                                  JOIN pg_class c ON c.oid = a.attrelid
                                                  JOIN pg_namespace n ON n.oid = c.relnamespace
                                         WHERE n.nspname = 'public'
                                           AND a.attnum > 0
                                           AND NOT a.attisdropped
                                           AND c.relname IN ('group_events', 'groups'))
                           SELECT COALESCE(e.column_name, g.column_name) || ' (' ||
                                  COALESCE(e.column_type, 'absent') || ' / ' || COALESCE(g.column_type, 'absent') || ')'
                           FROM      (SELECT column_name, column_type FROM cols
                                      WHERE table_name = 'group_events'
                                        AND column_name NOT IN ('id', 'time_inserted', 'event_type')) e
                           FULL JOIN (SELECT column_name, column_type FROM cols
                                      WHERE table_name = 'groups'
                                        AND column_name NOT IN ('source_event_row_id', 'time_applied')) g
                                  ON e.column_name = g.column_name
                           WHERE e.column_name IS NULL OR g.column_name IS NULL OR e.column_type <> g.column_type
                           """;

        // assert
        Assert.Empty(Strings(connectionString, sql));
    }

    /// <summary>
    /// The agreed schema, written out: the only test that fails if group_events is reordered or retyped.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the agreed columns and types in the agreed order")]
    public void Schema_Should_carry_the_agreed_columns_and_types_in_the_agreed_order()
    {
        // arrange
        var connectionString = Migrate();

        string[] agreed =
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
        ];

        // act
        var actual = Strings(connectionString,
            """
            SELECT a.attname || ' ' || format_type(a.atttypid, a.atttypmod) ||
                   CASE WHEN a.attnotnull THEN '' ELSE ' null' END
            FROM pg_attribute a
            WHERE a.attrelid = 'group_events'::regclass
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """);

        // assert
        Assert.Equal(agreed, actual);
    }

    [Fact(DisplayName = "Edge case Should let the last event of a batch win for one group")]
    public void Edge_case_Should_let_the_last_event_of_a_batch_win_for_one_group()
    {
        // arrange
        var connectionString = Migrate();

        // act, one statement carrying a creation, a modification and an unrelated group
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, defaultLeverage: 100, company: "v1"),
            StateEvent(connectionString, Sender, 26, version: 1, defaultLeverage: 200, company: "other"),
            StateEvent(connectionString, Sender, 25, version: 2, defaultLeverage: 50, company: "v2"));

        // assert
        Assert.Equal([(25L, 2L, 50, "v2"), (26L, 1L, 200, "other")], ReadGroups(connectionString));
    }

    [Fact(DisplayName = "Edge case Should never show a group created and deleted in one batch")]
    public void Edge_case_Should_never_show_a_group_created_and_deleted_in_one_batch()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            DeleteEvent(connectionString, Sender, 25));

        // assert
        Assert.Empty(ReadGroups(connectionString));
    }

    [Fact(DisplayName = "Edge case Should ignore a deletion for a group with no row")]
    public void Edge_case_Should_ignore_a_deletion_for_a_group_with_no_row()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));

        // act
        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 9999));

        // assert
        Assert.Single(ReadGroups(connectionString));
    }

    /// <summary>
    /// Why the sweep runs at the end marker: a resync spans several transactions.
    /// </summary>
    [Fact(DisplayName = "Workflow Should keep the table complete while a resync is running")]
    public void Workflow_Should_keep_the_table_complete_while_a_resync_is_running()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, Sender, 26, version: 1));

        // act, the start marker arrives in a transaction of its own
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));

        // assert
        Assert.Equal(2, ReadGroups(connectionString).Count);
        Assert.True(ResyncInProgress(connectionString, Sender));
    }

    [Fact(DisplayName = "Workflow Should converge the table on the live set a resync listed")]
    public void Workflow_Should_converge_the_table_on_the_live_set_a_resync_listed()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, Sender, 26, version: 1));

        // act, 26 was deleted while the sender was down, so the resync re-announces only 25
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Equal([25L], ReadGroups(connectionString).Select(group => group.GroupId));
        Assert.False(ResyncInProgress(connectionString, Sender));
    }

    [Fact(DisplayName = "Edge case Should remove nothing for an end marker with no start marker")]
    public void Edge_case_Should_remove_nothing_for_an_end_marker_with_no_start_marker()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));

        // act
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Single(ReadGroups(connectionString));
    }

    /// <summary>
    /// An AFTER ROW trigger sees the whole batch, so an unbounded max(id) found the next resync's start marker.
    /// </summary>
    [Fact(DisplayName = "Regression Should sweep against its own start marker when a batch carries two resyncs")]
    public void Regression_Should_sweep_against_its_own_start_marker_when_a_batch_carries_two_resyncs()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 100, version: 1),
            StateEvent(connectionString, Sender, 200, version: 1));

        // act, one statement ending the first resync and opening a second
        InsertOneStatement(connectionString,
            Marker(connectionString, Sender, eventType: 3),
            Marker(connectionString, Sender, eventType: 2));

        // assert, both were re-announced by the finished resync, so both survive
        Assert.Equal([100L, 200L], ReadGroups(connectionString).Select(group => group.GroupId));
    }

    [Fact(DisplayName = "Edge case Should store an unrecognised event type without projecting it")]
    public void Edge_case_Should_store_an_unrecognised_event_type_without_projecting_it()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1, defaultLeverage: 100));

        // act, raising here would abort the whole exporting COPY
        InsertOneStatement(connectionString,
            Event(connectionString, Sender, eventType: 99, ("group_id", "25"), ("version", "7"), ("company", "'unknown'")));

        // assert
        Assert.Equal(1, Scalar<long>(connectionString, "SELECT count(*) FROM group_events WHERE event_type = 99"));
        Assert.Equal([(25L, 1L, 100, "")], ReadGroups(connectionString));
    }

    /// <summary>
    /// No dedup key on the log, so the state table has to absorb a redelivery.
    /// </summary>
    [Fact(DisplayName = "Edge case Should leave the same state when an event is redelivered")]
    public void Edge_case_Should_leave_the_same_state_when_an_event_is_redelivered()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 3, defaultLeverage: 100, company: "v3"));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 3, defaultLeverage: 100, company: "v3"));

        // assert
        Assert.Equal(2, Scalar<long>(connectionString, "SELECT count(*) FROM group_events"));
        Assert.Equal([(25L, 3L, 100, "v3")], ReadGroups(connectionString));
    }

    /// <summary>
    /// A reused id restarts its version, so a version guard would keep showing the old group.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new group when an id is reused after deletion")]
    public void Edge_case_Should_show_the_new_group_when_an_id_is_reused_after_deletion()
    {
        // arrange
        var connectionString = Migrate();

        // act, the old group reaches version 3, is deleted, and a new one takes the id
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 800, version: 1, name: "real/EUR", defaultLeverage: 100, company: "old"),
            StateEvent(connectionString, Sender, 800, version: 3, name: "real/EUR", defaultLeverage: 100, company: "old"),
            DeleteEvent(connectionString, Sender, 800),
            StateEvent(connectionString, Sender, 800, version: 1, name: "demo/USD", defaultLeverage: 500, company: "new"));

        // assert
        Assert.Equal([(800L, 1L, 500, "new")], ReadGroups(connectionString));
        Assert.Equal("demo/USD", ReadGroupName(connectionString, 800));
    }

    /// <summary>
    /// The same, with the deletion missed while the sender was down.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new group when a reused id is first seen in a resync")]
    public void Edge_case_Should_show_the_new_group_when_a_reused_id_is_first_seen_in_a_resync()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 900, version: 4, name: "real/EUR", defaultLeverage: 100, company: "old"));

        // act
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 900, version: 1, name: "demo/USD", defaultLeverage: 500, company: "new"));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Equal([(900L, 1L, 500, "new")], ReadGroups(connectionString));
        Assert.Equal("demo/USD", ReadGroupName(connectionString, 900));
    }

    /// <summary>
    /// Why name is not unique: a unique index would fail this re-announcement and stall the export.
    /// </summary>
    [Fact(DisplayName = "Edge case Should hold two groups under one name while a resync is converging")]
    public void Edge_case_Should_hold_two_groups_under_one_name_while_a_resync_is_converging()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 5, version: 3, name: "real/FPlive/CHF_IN_NT_R_H", company: "old"));

        // act, 5 was deleted while the sender was down and its name re-created as 9
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 9, version: 1, name: "real/FPlive/CHF_IN_NT_R_H", company: "new"));

        // assert, both hold the name mid-resync
        const string byName = "SELECT group_id::text FROM groups WHERE name = 'real/FPlive/CHF_IN_NT_R_H' ORDER BY group_id";

        Assert.Equal(["5", "9"], Strings(connectionString, byName));
        Assert.True(ResyncInProgress(connectionString, Sender));

        // and the end marker sweeps the old one
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        Assert.Equal(["9"], Strings(connectionString, byName));
    }

    /// <summary>
    /// A float anywhere on the way would round the interest rate below.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep thresholds, deposits and rates as exact decimals")]
    public void Edge_case_Should_keep_thresholds_deposits_and_rates_as_exact_decimals()
    {
        // arrange
        var connectionString = Migrate();

        // act, the API's group 7 sample, plus a deposit and rate beyond a double's precision
        InsertOneStatement(connectionString,
            Event(connectionString, Sender, eventType: 0,
                ("group_id", "7"),
                ("version", "1"),
                ("name", "'real/FPlive/CHF_IN_NT_R_H'"),
                ("company", "'FP Trading LLC'"),
                ("currency", "'CHF'"),
                ("stopout", "50"),
                ("margin_call", "100"),
                ("demo_deposit", "10000.000000000000000001"),
                ("interest_rate", "-1.123456789012345678")));

        // assert
        Assert.Equal(["50", "100", "10000.000000000000000001", "-1.123456789012345678"], Strings(connectionString,
            "SELECT unnest(ARRAY[stopout, margin_call, demo_deposit, interest_rate])::text FROM groups WHERE group_id = 7"));
    }

    /// <summary>
    /// The one nullable column: a NOT NULL would stall the export, a COALESCE would keep a removed service.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep a group without an email service as NULL")]
    public void Edge_case_Should_keep_a_group_without_an_email_service_as_NULL()
    {
        // arrange
        var connectionString = Migrate();

        // act, one group created without an email service and one with
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, Sender, 26, version: 1, emailServiceId: 1));

        // assert
        Assert.Equal(["25: NULL", "26: 1"], ReadEmailServices(connectionString));

        // and a modification that removes the service clears it
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 26, version: 2));

        Assert.Equal(["25: NULL", "26: NULL"], ReadEmailServices(connectionString));
    }

    [Fact(DisplayName = "Edge case Should keep two senders in one database independent")]
    public void Edge_case_Should_keep_two_senders_in_one_database_independent()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, OtherSender, 5001, version: 1));

        // act, one sender completes a resync that re-announces nothing
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert, the other sender's group is untouched
        Assert.Equal([5001L], ReadGroups(connectionString).Select(group => group.GroupId));
    }

    /// <summary>
    /// Unlike the test above, this one fails if any write path is keyed on group_id alone.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep the same group id independent across senders")]
    public void Edge_case_Should_keep_the_same_group_id_independent_across_senders()
    {
        // arrange, one group id, two senders, different content
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, defaultLeverage: 100, company: "sender 7"),
            StateEvent(connectionString, OtherSender, 25, version: 9, defaultLeverage: 200, company: "sender 8"));

        Assert.Equal(2, ReadGroups(connectionString).Count);
        Assert.Equal("sender 7", ReadCompany(connectionString, Sender, 25));
        Assert.Equal("sender 8", ReadCompany(connectionString, OtherSender, 25));

        // act, one sender modifies the id, then deletes it, then resyncs re-announcing nothing
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 2, defaultLeverage: 300, company: "sender 7 v2"));
        Assert.Equal("sender 8", ReadCompany(connectionString, OtherSender, 25));

        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 25));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert, the other sender's group survives unchanged
        Assert.Equal(0, Scalar<long>(connectionString, $"SELECT count(*) FROM groups WHERE sender_id = {Sender}"));
        Assert.Equal([(25L, 9L, 200, "sender 8")], ReadGroups(connectionString));
    }

    /// <summary>
    /// Each sender numbers its own groups. The wrong join is asserted too, so the fixture keeps reproducing it.
    /// </summary>
    [Fact(DisplayName = "Invariant Should need sender_id as well as group_id to join trades to their group")]
    public void Invariant_Should_need_sender_id_as_well_as_group_id_to_join_trades_to_their_group()
    {
        // arrange, one group id, two senders, two different groups, trades on each
        var connectionString = Migrate();

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, name: "real/FPlive/CHF_IN_NT_R_H"),
            StateEvent(connectionString, OtherSender, 25, version: 1, name: "demo/USD"));

        Execute(connectionString,
            $"INSERT INTO trades ({Columns(connectionString, "trades")}) VALUES " +
            string.Join(", ",
                Trade(connectionString, Sender, tradeId: 1, groupId: 25, groupName: "real/FPlive/CHF_IN_NT_R_H"),
                Trade(connectionString, Sender, tradeId: 2, groupId: 25, groupName: "real/FPlive/CHF_IN_NT_R_H"),
                Trade(connectionString, OtherSender, tradeId: 1, groupId: 25, groupName: "demo/USD")));

        // act, the full key against the id alone
        const string fullKey = "FROM trades t JOIN groups g ON g.sender_id = t.sender_id AND g.group_id = t.group_id";
        const string idOnly = "FROM trades t JOIN groups g ON g.group_id = t.group_id";

        // assert, three trades have a group, and joining on the id alone invents three more
        Assert.Equal(3, Scalar<long>(connectionString, $"SELECT count(*) {fullKey}"));
        Assert.Equal(6, Scalar<long>(connectionString, $"SELECT count(*) {idOnly}"));

        // and only the full key pairs each trade with its own group
        Assert.Equal(0, Scalar<long>(connectionString, $"SELECT count(*) {fullKey} WHERE g.name <> t.group_name"));
        Assert.Equal(3, Scalar<long>(connectionString, $"SELECT count(*) {idOnly} WHERE g.name <> t.group_name"));
    }

    /// <summary>
    /// A sweep leaves no event, so a naive reconcile would replay the swept group's last state.
    /// </summary>
    [Fact(DisplayName = "Regression Should not bring back a swept group when the projection runs again")]
    public void Regression_Should_not_bring_back_a_swept_group_when_the_projection_runs_again()
    {
        // arrange, 26 is swept by a completed resync, leaving 25 live
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, Sender, 26, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // and then the trigger is dropped by hand while the export carries on
        Execute(connectionString, "DROP TRIGGER trg_project_groups ON group_events");
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 6001, version: 1, defaultLeverage: 400, company: "while broken"));
        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 25));

        Assert.Equal([25L], ReadGroups(connectionString).Select(group => group.GroupId));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the drift is healed, the trigger is back, and the swept group stays swept
        Assert.Equal([(6001L, 1L, 400, "while broken")], ReadGroups(connectionString));
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_project_groups' AND NOT tgisinternal"));
    }

    /// <summary>
    /// The sweep lives in the trigger, so the reconcile has to apply one the trigger missed.
    /// </summary>
    [Fact(DisplayName = "Regression Should apply a resync sweep that happened while the trigger was absent")]
    public void Regression_Should_apply_a_resync_sweep_that_happened_while_the_trigger_was_absent()
    {
        // arrange, two live groups, both projected
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 101, version: 1, company: "re-announced"),
            StateEvent(connectionString, Sender, 102, version: 1, company: "dropped by the resync"));

        Assert.Equal(2, ReadGroups(connectionString).Count);

        // act, the trigger is gone for the whole resync, which re-announces 101 only
        Execute(connectionString, "DROP TRIGGER trg_project_groups ON group_events");

        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 101, version: 1, company: "re-announced"));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        Assert.Equal(2, ReadGroups(connectionString).Count);

        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Equal([(101L, 1L, 0, "re-announced")], ReadGroups(connectionString));
    }

    /// <summary>
    /// The sender dies mid-resync, so no end marker arrives.
    /// </summary>
    [Fact(DisplayName = "Edge case Should leave the table converging when a resync never finishes")]
    public void Edge_case_Should_leave_the_table_converging_when_a_resync_never_finishes()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, company: "before"),
            StateEvent(connectionString, Sender, 26, version: 1, company: "not reached"));

        // act, the resync starts and re-announces one group, then the sender dies
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 2, company: "re-announced"));

        // assert, the unreached group keeps its old state and the sender stays flagged
        Assert.Equal([(25L, 2L, 0, "re-announced"), (26L, 1L, 0, "not reached")], ReadGroups(connectionString));
        Assert.True(ResyncInProgress(connectionString, Sender));
    }

    /// <summary>
    /// Drives all four column lists with values distinct per column and per write, comparing rows as jsonb.
    /// </summary>
    [Fact(DisplayName = "Invariant Should copy every field from the event unchanged on all four write paths")]
    public void Invariant_Should_copy_every_field_from_the_event_unchanged_on_all_four_write_paths()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        Assert.True(EventSchema(connectionString).Count(column => column.Type == "boolean") < 1 << BooleanPatterns,
            $"More booleans than {BooleanPatterns} patterns can tell apart; raise BooleanPatterns.");

        // act, the trigger's INSERT
        InsertOneStatement(connectionString, DistinctiveStateEvents(connectionString, firstGroupId: 101, seed: 1, flip: false));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // act, the trigger's SET
        InsertOneStatement(connectionString, DistinctiveStateEvents(connectionString, firstGroupId: 101, seed: 2, flip: true));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // act, the reconcile's SET (101-105) and INSERT (201-205)
        Execute(connectionString, "DROP TRIGGER trg_project_groups ON group_events");

        InsertOneStatement(connectionString, DistinctiveStateEvents(connectionString, firstGroupId: 101, seed: 3, flip: false));
        InsertOneStatement(connectionString, DistinctiveStateEvents(connectionString, firstGroupId: 201, seed: 3, flip: false));

        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // and the rows compared were populated by the last write
        Assert.Equal(2 * BooleanPatterns, Scalar<long>(connectionString,
            "SELECT count(*) FROM groups WHERE company LIKE '3-%' AND stopout <> 0"));
    }

    [Fact(DisplayName = "Invariant Should name the log event that produced every state row")]
    public void Invariant_Should_name_the_log_event_that_produced_every_state_row()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 2));

        // assert, stamped with the newest state event
        const string notNewest = """
                                 SELECT g.group_id::text
                                 FROM groups g
                                          JOIN LATERAL (SELECT id
                                                        FROM group_events e
                                                        WHERE e.sender_id = g.sender_id
                                                          AND e.group_id = g.group_id
                                                          AND e.event_type = 0
                                                        ORDER BY e.id DESC
                                                        LIMIT 1) newest ON true
                                 WHERE g.source_event_row_id <> newest.id
                                 """;

        Assert.Empty(Strings(connectionString, notNewest));
    }

    /// <summary>
    /// Why the state is a table, not a view: it has to outlive the log's retention.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep the current state when the log is purged")]
    public void Invariant_Should_keep_the_current_state_when_the_log_is_purged()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 2, defaultLeverage: 100, company: "survives"),
            StateEvent(connectionString, Sender, 26, version: 1, defaultLeverage: 200, company: "also survives"));

        // act, a purge is a DELETE; the trigger fires on INSERT only
        Execute(connectionString, "DELETE FROM group_events");

        // assert
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM group_events"));
        Assert.Equal([(25L, 2L, 100, "survives"), (26L, 1L, 200, "also survives")], ReadGroups(connectionString));
    }

    [Fact(DisplayName = "Invariant Should key the state table on sender and group")]
    public void Invariant_Should_key_the_state_table_on_sender_and_group()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        const string primaryKey = """
                                  SELECT string_agg(a.attname, ', ' ORDER BY k.ord)
                                  FROM pg_index i
                                           JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
                                           JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                                  WHERE i.indrelid = 'groups'::regclass
                                    AND i.indisprimary
                                  """;

        Assert.Equal("sender_id, group_id", Scalar<string>(connectionString, primaryKey));
    }

    /// <summary>
    /// No lag: the state row is written in the event's transaction.
    /// </summary>
    [Fact(DisplayName = "Invariant Should project inside the writing transaction and roll back with it")]
    public void Invariant_Should_project_inside_the_writing_transaction_and_roll_back_with_it()
    {
        // arrange
        var connectionString = Migrate();
        var columns = EventColumns(connectionString);
        var row = StateEvent(connectionString, Sender, 25, version: 1);

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // act
        using (var transaction = connection.BeginTransaction())
        {
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO group_events ({columns}) VALUES {row}";
                insert.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT count(*) FROM groups";

            // assert, visible before commit
            Assert.Equal(1L, (long)read.ExecuteScalar()!);

            transaction.Rollback();
        }

        // and gone after rollback
        Assert.Empty(ReadGroups(connectionString));
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM group_events"));
    }

    /// <summary>
    /// A correctly stamped, wrong row, as a buggy projection would leave; re-applying the file must fix it.
    /// </summary>
    [Fact(DisplayName = "Regression Should correct a state row whose content disagrees with its event")]
    public void Regression_Should_correct_a_state_row_whose_content_disagrees_with_its_event()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, defaultLeverage: 100, company: "correct"));

        // right stamp, wrong content
        Execute(connectionString, "UPDATE groups SET default_leverage = 999, company = 'corrupted'");
        Assert.Equal([(25L, 1L, 999, "corrupted")], ReadGroups(connectionString));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the log wins
        Assert.Equal([(25L, 1L, 100, "correct")], ReadGroups(connectionString));
    }

    /// <summary>
    /// The production path: the exporter over <c>COPY</c>, including a DELETE sweep and a <c>\N</c>.
    /// </summary>
    [Fact(DisplayName = "Grants Should let the exporter role drive the whole projection over COPY")]
    public void Grants_Should_let_the_exporter_role_drive_the_whole_projection_over_COPY()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var columns = EventColumns(request.ConnectionString);
        var schema = EventSchema(request.ConnectionString);

        using var exporter = new NpgsqlConnection(ExporterConnectionString(request));
        exporter.Open();

        // act
        Copy(exporter, columns,
            CopyLine(schema, Sender, eventType: 0, groupId: 25, version: 1, defaultLeverage: 100, company: "live", emailServiceId: 1),
            CopyLine(schema, Sender, eventType: 0, groupId: 26, version: 1, defaultLeverage: 200, company: "swept"));

        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 2));

        // assert
        Assert.Equal([25L, 26L], ReadGroups(request.ConnectionString).Select(group => group.GroupId));
        Assert.Equal(["25: 1", "26: NULL"], ReadEmailServices(request.ConnectionString));

        // the sweep needs DELETE as the exporter
        Copy(exporter, columns,
            CopyLine(schema, Sender, eventType: 0, groupId: 25, version: 1, defaultLeverage: 100, company: "live", emailServiceId: 1));
        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 3));

        Assert.Equal([(25L, 1L, 100, "live")], ReadGroups(request.ConnectionString));

        // and ro reads but cannot write
        using var reader = new NpgsqlConnection(ReaderConnectionString(request));
        reader.Open();

        using (var read = reader.CreateCommand())
        {
            read.CommandText = "SELECT count(*) FROM groups";
            Assert.Equal(1L, (long)read.ExecuteScalar()!);
        }

        using (var read = reader.CreateCommand())
        {
            read.CommandText = $"SELECT resync_in_progress FROM groups_resync WHERE sender_id = {Sender}";
            Assert.False((bool)read.ExecuteScalar()!);
        }

        using var write = reader.CreateCommand();
        write.CommandText = "DELETE FROM groups";
        var denied = Assert.Throws<PostgresException>(() => write.ExecuteNonQuery());
        Assert.Equal("42501", denied.SqlState);
    }

    // ---------------------------------------------------------------------------------------------------
    // Fixtures: rows are built from the live schema, zeroed, with the named columns overridden.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A table's columns in ordinal order, minus id and time_inserted.
    /// </summary>
    private static List<(string Column, string Type, bool Nullable)> Schema(string connectionString, string table)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT column_name, data_type, is_nullable = 'YES'
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = @table
                                AND column_name NOT IN ('id', 'time_inserted')
                              ORDER BY ordinal_position
                              """;
        command.Parameters.AddWithValue("table", table);

        using var reader = command.ExecuteReader();

        var columns = new List<(string, string, bool)>();

        while (reader.Read())
        {
            columns.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return columns;
    }

    private static List<(string Column, string Type, bool Nullable)> EventSchema(string connectionString)
    {
        return Schema(connectionString, "group_events");
    }

    private static string Columns(string connectionString, string table)
    {
        return string.Join(", ", Schema(connectionString, table).Select(column => column.Column));
    }

    private static string EventColumns(string connectionString)
    {
        return Columns(connectionString, "group_events");
    }

    /// <summary>
    /// The exporter's zero for an unused field as a SQL literal; NULL where nullable.
    /// </summary>
    private static string Zero((string Column, string Type, bool Nullable) column)
    {
        if (column.Nullable)
        {
            return "NULL";
        }

        return column.Type switch
        {
            "character varying" => "''",
            "timestamp with time zone" => "to_timestamp(0)",
            "boolean" => "false",
            _ => "0"
        };
    }

    /// <summary>
    /// One row as a SQL tuple, zeroed, with the named columns overridden by SQL literals.
    /// </summary>
    private static string Row(string connectionString, string table, params (string Column, string Value)[] overrides)
    {
        var values = Schema(connectionString, table).Select(column =>
            overrides.FirstOrDefault(o => o.Column == column.Column).Value ?? Zero(column));

        return $"({string.Join(", ", values)})";
    }

    private static string Event(string connectionString, long sender, int eventType, params (string Column, string Value)[] overrides)
    {
        return Row(connectionString, "group_events",
            [("sender_id", sender.ToString()), ("event_type", eventType.ToString()), .. overrides]);
    }

    /// <summary>
    /// A state event; the named fields stand in for the whole record.
    /// </summary>
    private static string StateEvent(string connectionString, long sender, long groupId, long version,
        string name = "", int defaultLeverage = 0, string company = "", long? emailServiceId = null)
    {
        return Event(connectionString, sender, eventType: 0,
            ("group_id", groupId.ToString()),
            ("version", version.ToString()),
            ("name", Quote(name)),
            ("default_leverage", defaultLeverage.ToString()),
            ("company", Quote(company)),
            ("email_service_id", emailServiceId?.ToString() ?? "NULL"));
    }

    private static string DeleteEvent(string connectionString, long sender, long groupId)
    {
        return Event(connectionString, sender, eventType: 1, ("group_id", groupId.ToString()));
    }

    private static string Marker(string connectionString, long sender, int eventType)
    {
        return Event(connectionString, sender, eventType);
    }

    /// <summary>
    /// One full state event per boolean pattern. <paramref name="seed"/> changes the non-booleans;
    /// <paramref name="flip"/> inverts the booleans and which events hold NULL.
    /// </summary>
    private static string[] DistinctiveStateEvents(string connectionString, long firstGroupId, int seed, bool flip)
    {
        var schema = EventSchema(connectionString);

        var booleans = schema
            .Where(column => column.Type == "boolean")
            .Select((column, index) => (column.Column, Bits: index + 1))
            .ToDictionary(boolean => boolean.Column, boolean => boolean.Bits);

        return Enumerable.Range(0, BooleanPatterns).Select(pattern =>
        {
            var overrides = schema
                .Where(column => column.Column is not ("sender_id" or "event_type" or "group_id"))
                .Select((column, ordinal) => (column.Column, Value: column switch
                {
                    // alternate events, so every path writes value -> NULL and back
                    { Nullable: true } when (pattern % 2 == 1) != flip => "NULL",
                    { Type: "character varying" } => $"'{seed}-{ordinal}''s'",
                    { Type: "timestamp with time zone" } => $"to_timestamp({1700000000 + seed * 1000 + ordinal})",
                    // more digits than a double holds
                    { Type: "numeric" } => $"{seed * 1000 + ordinal}.000000000000000001",
                    { Type: "boolean" } => (((booleans[column.Column] >> pattern) & 1) == 1) != flip ? "true" : "false",
                    // fits SMALLINT
                    _ => (seed * 1000 + ordinal + 1).ToString()
                }))
                .ToArray();

            return Event(connectionString, Sender, eventType: 0,
                [("group_id", (firstGroupId + pattern).ToString()), .. overrides]);
        }).ToArray();
    }

    /// <summary>
    /// Columns where a state row differs from its source event, as <c>group_id: column</c>.
    /// </summary>
    private static List<string> Mismatches(string connectionString)
    {
        return Strings(connectionString,
            """
            SELECT g.group_id || ': ' || key
            FROM groups g
                     JOIN group_events e ON e.id = g.source_event_row_id
                     JOIN LATERAL jsonb_each(to_jsonb(e) - 'id' - 'time_inserted' - 'event_type') ON true
            WHERE value IS DISTINCT FROM ((to_jsonb(g) - 'source_event_row_id' - 'time_applied') -> key)
            ORDER BY g.group_id, key
            """);
    }

    private static string Trade(string connectionString, long sender, long tradeId, long groupId, string groupName)
    {
        return Row(connectionString, "trades",
            ("sender_id", sender.ToString()),
            ("trade_id", tradeId.ToString()),
            ("group_id", groupId.ToString()),
            ("group_name", Quote(groupName)));
    }

    private static string Quote(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }

    /// <summary>
    /// One event as a <c>COPY</c> text line; <c>\N</c> only for a missing email service.
    /// </summary>
    private static string CopyLine(List<(string Column, string Type, bool Nullable)> schema, long sender, int eventType,
        long groupId = 0, long version = 0, int defaultLeverage = 0, string company = "", long? emailServiceId = null)
    {
        return string.Join('\t', schema.Select(column => column.Column switch
        {
            "sender_id" => sender.ToString(),
            "event_type" => eventType.ToString(),
            "group_id" => groupId.ToString(),
            "version" => version.ToString(),
            "default_leverage" => defaultLeverage.ToString(),
            "company" => company,
            "email_service_id" => emailServiceId?.ToString() ?? @"\N",
            _ => column.Type switch
            {
                "character varying" => "",
                "boolean" => "f",
                _ => "0"
            }
        }));
    }

    /// <summary>
    /// Sends rows the way the export does, over <c>COPY … FROM STDIN</c>.
    /// </summary>
    private static void Copy(NpgsqlConnection connection, string columns, params string[] rows)
    {
        using var writer = connection.BeginTextImport($"COPY group_events ({columns}) FROM STDIN");

        foreach (var row in rows)
        {
            writer.Write(row);
            writer.Write('\n');
        }
    }

    /// <summary>
    /// One statement and one transaction, the shape of an export batch.
    /// </summary>
    private static void InsertOneStatement(string connectionString, params string[] rows)
    {
        Execute(connectionString,
            $"INSERT INTO group_events ({EventColumns(connectionString)}) VALUES {string.Join(", ", rows)}");
    }

    private string ExporterConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "exporter"));
    }

    private string ReaderConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "ro"));
    }

    private static string WithUser(string connectionString, DatabaseUser user)
    {
        return new NpgsqlConnectionStringBuilder(connectionString) { Username = user.User, Password = user.Password }.ConnectionString;
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<(long GroupId, long Version, int DefaultLeverage, string Company)> ReadGroups(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_id, version, default_leverage, company FROM groups ORDER BY group_id, sender_id";

        using var reader = command.ExecuteReader();

        var groups = new List<(long, long, int, string)>();

        while (reader.Read())
        {
            groups.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return groups;
    }

    /// <summary>
    /// Each group's email service, as <c>group_id: email_service_id</c>, with NULL spelt out.
    /// </summary>
    private static List<string> ReadEmailServices(string connectionString)
    {
        return Strings(connectionString,
            "SELECT group_id || ': ' || COALESCE(email_service_id::text, 'NULL') FROM groups ORDER BY group_id, sender_id");
    }

    private static string ReadGroupName(string connectionString, long groupId)
    {
        return Scalar<string>(connectionString, $"SELECT name FROM groups WHERE group_id = {groupId}");
    }

    private static string ReadCompany(string connectionString, long sender, long groupId)
    {
        return Scalar<string>(connectionString,
            $"SELECT company FROM groups WHERE sender_id = {sender} AND group_id = {groupId}");
    }

    private static bool ResyncInProgress(string connectionString, long sender)
    {
        return Scalar<bool>(connectionString, $"SELECT resync_in_progress FROM groups_resync WHERE sender_id = {sender}");
    }

    private static bool TableExists(string connectionString, string tableName)
    {
        return Scalar<long>(connectionString,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{tableName}'") == 1;
    }

    private static bool IndexExists(string connectionString, string indexName)
    {
        return Scalar<long>(connectionString,
            $"SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = '{indexName}'") == 1;
    }

    private static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static List<string> Strings(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        var values = new List<string>();

        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// Provisions and migrates a database; returns its connection string.
    /// </summary>
    private string Migrate()
    {
        var request = CreateRequest();

        AssertSuccessful(CreateRunner().Run(request));

        return request.ConnectionString;
    }

    private DbUpRunner CreateRunner()
    {
        return new(NullLogger<DbUpRunner>.Instance, new TestDbUpLogProvider(output));
    }

    private DbMigratorRequest CreateRequest()
    {
        var configured = configBuilder.PostgresConnectionString;

        Assert.False(string.IsNullOrWhiteSpace(configured), "No PostgreSQL connection string configured. Start docker-compose.yml or set Postgres__ConnectionString.");

        var databaseName = $"group_projection_{Guid.NewGuid():N}"[..40];

        provisionedDatabases.Add((configured!, databaseName));

        return new(
            DatabaseProvider.Postgres,
            WithDatabase(configured!, databaseName),
            databaseName,
            "schema_versions",
            [new(true, "psql")],
            configBuilder.RepositoryRoot,
            // the CLI's own variables, so new ones reach these tests automatically
            MigrationHandler.BuildVariables(DatabaseProvider.Postgres, databaseName, null, new()),
            false,
            false,
            null,
            [
                new() { Access = "exporter", User = $"{databaseName}_exporter", Password = "passw0rd" },
                new() { Access = "ro", User = $"{databaseName}_reader", Password = "passw0rd" }
            ]);
    }

    private static string WithDatabase(string connectionString, string databaseName)
    {
        return new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
    }

    private static void AssertSuccessful(IEnumerable<(string SubFolder, DatabaseUpgradeResult Result)> results)
    {
        foreach (var (subFolder, result) in results)
        {
            Assert.True(result.Successful, $"{subFolder} failed on '{result.ErrorScript?.Name}': {result.Error}");
        }
    }
}
