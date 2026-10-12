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
/// The trigger that maintains the clients table from the client event log.
/// </summary>
/// <remarks>
/// Mirrors <see cref="TradingAccountsProjectionIntegrationTests"/>. Rows are built from the live schema, so no
/// column list is kept by hand. Each test gets its own database.
/// </remarks>
[Trait("Category", "Integration")]
public class ClientsProjectionIntegrationTests : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    private const long Sender = 7;
    private const long OtherSender = 8;

    private readonly ConfigBuilder configBuilder;
    private readonly ITestOutputHelper output;
    private readonly List<(string ConnectionString, string DatabaseName)> provisionedDatabases = [];

    public ClientsProjectionIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output)
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
        Assert.True(TableExists(connectionString, "client_events"));
        Assert.True(TableExists(connectionString, "clients"));
        Assert.True(TableExists(connectionString, "clients_resync"));

        Assert.True(IndexExists(connectionString, "idx_client_row_client_events"));
        Assert.True(IndexExists(connectionString, "idx_markers_client_events"));

        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_project_clients' AND NOT tgisinternal"));
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_proc WHERE proname = 'project_clients'"));
    }

    /// <summary>
    /// A type or nullability drift would not fail the migration; it would round, truncate or stall the export.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the same client columns in the log and the state table")]
    public void Schema_Should_carry_the_same_client_columns_in_the_log_and_the_state_table()
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
                                           AND c.relname IN ('client_events', 'clients'))
                           SELECT COALESCE(e.column_name, c.column_name) || ' (' ||
                                  COALESCE(e.column_type, 'absent') || ' / ' || COALESCE(c.column_type, 'absent') || ')'
                           FROM      (SELECT column_name, column_type FROM cols
                                      WHERE table_name = 'client_events'
                                        AND column_name NOT IN ('id', 'time_inserted', 'event_type')) e
                           FULL JOIN (SELECT column_name, column_type FROM cols
                                      WHERE table_name = 'clients'
                                        AND column_name NOT IN ('source_event_row_id', 'time_applied')) c
                                  ON e.column_name = c.column_name
                           WHERE e.column_name IS NULL OR c.column_name IS NULL OR e.column_type <> c.column_type
                           """;

        // assert
        Assert.Empty(Strings(connectionString, sql));
    }

    /// <summary>
    /// The agreed schema, written out: the only test that fails if client_events is reordered or retyped.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the agreed columns and types in the agreed order")]
    public void Schema_Should_carry_the_agreed_columns_and_types_in_the_agreed_order()
    {
        // arrange
        var connectionString = Migrate();

        string[] agreed =
        [
            "id bigint", "time_inserted timestamp with time zone", "sender_id bigint", "event_type smallint",
            "client_id bigint", "version bigint", "client_type integer", "client_status integer",
            "assigned_manager bigint", "time_created timestamp with time zone",
            "time_modified timestamp with time zone", "compliance_approved_by bigint",
            "compliance_client_category character varying(64)", "compliance_date_approval timestamp with time zone",
            "compliance_date_termination timestamp with time zone", "lead_campaign character varying(64)",
            "lead_source character varying(64)", "introducer bigint", "person_title character varying(32)",
            "person_first_name character varying(32)", "person_middle_name character varying(64)",
            "person_last_name character varying(32)", "person_birth_date timestamp with time zone",
            "person_citizenship character varying(64)", "person_tax_id character varying(64)",
            "person_document_type character varying(32)", "person_document_number character varying(32)",
            "person_document_date timestamp with time zone",
            "person_document_expiration_date timestamp with time zone", "person_document_extra character varying(64)",
            "person_employment integer", "person_industry integer", "person_education integer",
            "person_wealth_source integer", "person_annual_income double precision",
            "person_annual_income_currency character varying(16)", "person_net_assets double precision",
            "person_gender integer", "preferred_communication integer", "preferred_language character varying(16)",
            "company_name character varying(64)", "company_registration_number character varying(64)",
            "company_registration_date timestamp with time zone",
            "company_registration_authority character varying(64)", "company_vat character varying(32)",
            "company_lei character varying(32)", "company_license_number character varying(32)",
            "company_license_authority character varying(64)", "company_country character varying(32)",
            "company_registration_address character varying(64)", "company_website character varying(64)",
            "email character varying(64)", "phone character varying(32)", "messengers character varying(128)",
            "social_media character varying(128)", "last_contact_date timestamp with time zone",
            "address_country character varying(32)", "address_postcode character varying(16)",
            "address_street character varying(128)", "address_state character varying(64)",
            "address_city character varying(64)", "client_external_id character varying(64)",
            "comment character varying(256)", "experience_forex integer", "experience_cfd integer",
            "experience_futures integer", "experience_stocks integer", "experience_crypto integer"
        ];

        // act
        var actual = Strings(connectionString,
            """
            SELECT a.attname || ' ' || format_type(a.atttypid, a.atttypmod) ||
                   CASE WHEN a.attnotnull THEN '' ELSE ' null' END
            FROM pg_attribute a
            WHERE a.attrelid = 'client_events'::regclass
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """);

        // assert
        Assert.Equal(agreed, actual);
    }

    [Fact(DisplayName = "Edge case Should let the last event of a batch win for one client")]
    public void Edge_case_Should_let_the_last_event_of_a_batch_win_for_one_client()
    {
        // arrange
        var connectionString = Migrate();

        // act, one statement carrying a creation, a modification and an unrelated client
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 1, experience: 10, comment: "v1"),
            StateEvent(connectionString, Sender, 2003, version: 1, experience: 20, comment: "other"),
            StateEvent(connectionString, Sender, 1002, version: 2, experience: 5, comment: "v2"));

        // assert
        Assert.Equal([(1002L, 2L, 5, "v2"), (2003L, 1L, 20, "other")], ReadClients(connectionString));
    }

    [Fact(DisplayName = "Edge case Should never show a client created and deleted in one batch")]
    public void Edge_case_Should_never_show_a_client_created_and_deleted_in_one_batch()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 1),
            DeleteEvent(connectionString, Sender, 1002));

        // assert
        Assert.Empty(ReadClients(connectionString));
    }

    [Fact(DisplayName = "Edge case Should ignore a deletion for a client with no row")]
    public void Edge_case_Should_ignore_a_deletion_for_a_client_with_no_row()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 1));

        // act
        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 9999));

        // assert
        Assert.Single(ReadClients(connectionString));
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
            StateEvent(connectionString, Sender, 1002, version: 1),
            StateEvent(connectionString, Sender, 2003, version: 1));

        // act, the start marker arrives in a transaction of its own
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));

        // assert
        Assert.Equal(2, ReadClients(connectionString).Count);
        Assert.True(ResyncInProgress(connectionString, Sender));
    }

    [Fact(DisplayName = "Workflow Should converge the table on the live set a resync listed")]
    public void Workflow_Should_converge_the_table_on_the_live_set_a_resync_listed()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 1),
            StateEvent(connectionString, Sender, 2003, version: 1));

        // act, 2003 was deleted while the sender was down, so the resync re-announces only 1002
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Equal([1002L], ReadClients(connectionString).Select(client => client.ClientId));
        Assert.False(ResyncInProgress(connectionString, Sender));
    }

    [Fact(DisplayName = "Edge case Should remove nothing for an end marker with no start marker")]
    public void Edge_case_Should_remove_nothing_for_an_end_marker_with_no_start_marker()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 1));

        // act
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Single(ReadClients(connectionString));
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
        Assert.Equal([100L, 200L], ReadClients(connectionString).Select(client => client.ClientId));
    }

    [Fact(DisplayName = "Edge case Should store an unrecognised event type without projecting it")]
    public void Edge_case_Should_store_an_unrecognised_event_type_without_projecting_it()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 1, experience: 10));

        // act, raising here would abort the whole exporting COPY
        InsertOneStatement(connectionString,
            Event(connectionString, Sender, eventType: 99, ("client_id", "1002"), ("version", "7"), ("comment", "'unknown'")));

        // assert
        Assert.Equal(1, Scalar<long>(connectionString, "SELECT count(*) FROM client_events WHERE event_type = 99"));
        Assert.Equal([(1002L, 1L, 10, "")], ReadClients(connectionString));
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
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 3, experience: 25, comment: "v3"));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 3, experience: 25, comment: "v3"));

        // assert
        Assert.Equal(2, Scalar<long>(connectionString, "SELECT count(*) FROM client_events"));
        Assert.Equal([(1002L, 3L, 25, "v3")], ReadClients(connectionString));
    }

    /// <summary>
    /// A reused id restarts its version, so a version guard would keep showing the old client.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new client when an id is reused after deletion")]
    public void Edge_case_Should_show_the_new_client_when_an_id_is_reused_after_deletion()
    {
        // arrange
        var connectionString = Migrate();

        // act, the old client reaches version 3, is deleted, and a new one takes the id
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 800, version: 1, manager: 11, experience: 10, comment: "old"),
            StateEvent(connectionString, Sender, 800, version: 3, manager: 11, experience: 30, comment: "old"),
            DeleteEvent(connectionString, Sender, 800),
            StateEvent(connectionString, Sender, 800, version: 1, manager: 99, experience: 5, comment: "new"));

        // assert
        Assert.Equal([(800L, 1L, 5, "new")], ReadClients(connectionString));
        Assert.Equal(99, ReadAssignedManager(connectionString, 800));
    }

    /// <summary>
    /// The same, with the deletion missed while the sender was down.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new client when a reused id is first seen in a resync")]
    public void Edge_case_Should_show_the_new_client_when_a_reused_id_is_first_seen_in_a_resync()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 900, version: 4, manager: 22, experience: 40, comment: "old"));

        // act
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 900, version: 1, manager: 33, experience: 2, comment: "new"));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Equal([(900L, 1L, 2, "new")], ReadClients(connectionString));
        Assert.Equal(33, ReadAssignedManager(connectionString, 900));
    }

    [Fact(DisplayName = "Edge case Should keep two senders in one database independent")]
    public void Edge_case_Should_keep_two_senders_in_one_database_independent()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 1),
            StateEvent(connectionString, OtherSender, 5001, version: 1));

        // act, one sender completes a resync that re-announces nothing
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert, the other sender's client is untouched
        Assert.Equal([5001L], ReadClients(connectionString).Select(client => client.ClientId));
    }

    /// <summary>
    /// Unlike the test above, this one fails if any write path is keyed on client_id alone.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep the same client id independent across senders")]
    public void Edge_case_Should_keep_the_same_client_id_independent_across_senders()
    {
        // arrange, one client id, two senders, different content
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 1, experience: 10, comment: "sender 7"),
            StateEvent(connectionString, OtherSender, 1002, version: 9, experience: 20, comment: "sender 8"));

        Assert.Equal(2, ReadClients(connectionString).Count);
        Assert.Equal("sender 7", ReadComment(connectionString, Sender, 1002));
        Assert.Equal("sender 8", ReadComment(connectionString, OtherSender, 1002));

        // act, one sender modifies the id, then deletes it, then resyncs re-announcing nothing
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 2, experience: 11, comment: "sender 7 v2"));
        Assert.Equal("sender 8", ReadComment(connectionString, OtherSender, 1002));

        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 1002));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert, the other sender's client survives unchanged
        Assert.Equal(0, Scalar<long>(connectionString, $"SELECT count(*) FROM clients WHERE sender_id = {Sender}"));
        Assert.Equal([(1002L, 9L, 20, "sender 8")], ReadClients(connectionString));
    }

    /// <summary>
    /// Each sender numbers its own clients. The wrong join is asserted too, so the fixture keeps reproducing it.
    /// </summary>
    [Fact(DisplayName = "Invariant Should need sender_id as well as client_id to join clients to their accounts")]
    public void Invariant_Should_need_sender_id_as_well_as_client_id_to_join_clients_to_their_accounts()
    {
        // arrange, one client id, two senders, two different clients, accounts tagged with their owner
        var connectionString = Migrate();

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 1, comment: "Smith"),
            StateEvent(connectionString, OtherSender, 1002, version: 1, comment: "Jones"));

        Execute(connectionString,
            $"INSERT INTO trading_account_events ({Columns(connectionString, "trading_account_events")}) VALUES " +
            string.Join(", ",
                TradingAccountEvent(connectionString, Sender, tradingAccountId: 5001, clientId: 1002, owner: "Smith"),
                TradingAccountEvent(connectionString, Sender, tradingAccountId: 5002, clientId: 1002, owner: "Smith"),
                TradingAccountEvent(connectionString, OtherSender, tradingAccountId: 6001, clientId: 1002, owner: "Jones")));

        // act, the full key against the id alone
        const string fullKey = "FROM clients c JOIN trading_accounts a ON a.sender_id = c.sender_id AND a.client_id = c.client_id";
        const string idOnly = "FROM clients c JOIN trading_accounts a ON a.client_id = c.client_id";

        // assert, three accounts have a client, and joining on the id alone invents three more
        Assert.Equal(3, Scalar<long>(connectionString, $"SELECT count(*) {fullKey}"));
        Assert.Equal(6, Scalar<long>(connectionString, $"SELECT count(*) {idOnly}"));

        // and only the full key pairs each account with its own client
        Assert.Equal(0, Scalar<long>(connectionString, $"SELECT count(*) {fullKey} WHERE c.comment <> a.comment"));
        Assert.Equal(3, Scalar<long>(connectionString, $"SELECT count(*) {idOnly} WHERE c.comment <> a.comment"));
    }

    /// <summary>
    /// A sweep leaves no event, so a naive reconcile would replay the swept client's last state.
    /// </summary>
    [Fact(DisplayName = "Regression Should not bring back a swept client when the projection runs again")]
    public void Regression_Should_not_bring_back_a_swept_client_when_the_projection_runs_again()
    {
        // arrange, 2003 is swept by a completed resync, leaving 1002 live
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 1),
            StateEvent(connectionString, Sender, 2003, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // and then the trigger is dropped by hand while the export carries on
        Execute(connectionString, "DROP TRIGGER trg_project_clients ON client_events");
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 6001, version: 1, experience: 40, comment: "while broken"));
        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 1002));

        Assert.Equal([1002L], ReadClients(connectionString).Select(client => client.ClientId));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the drift is healed, the trigger is back, and the swept client stays swept
        Assert.Equal([(6001L, 1L, 40, "while broken")], ReadClients(connectionString));
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_project_clients' AND NOT tgisinternal"));
    }

    /// <summary>
    /// The sweep lives in the trigger, so the reconcile has to apply one the trigger missed.
    /// </summary>
    [Fact(DisplayName = "Regression Should apply a resync sweep that happened while the trigger was absent")]
    public void Regression_Should_apply_a_resync_sweep_that_happened_while_the_trigger_was_absent()
    {
        // arrange, two live clients, both projected
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 101, version: 1, comment: "re-announced"),
            StateEvent(connectionString, Sender, 102, version: 1, comment: "dropped by the resync"));

        Assert.Equal(2, ReadClients(connectionString).Count);

        // act, the trigger is gone for the whole resync, which re-announces 101 only
        Execute(connectionString, "DROP TRIGGER trg_project_clients ON client_events");

        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 101, version: 1, comment: "re-announced"));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        Assert.Equal(2, ReadClients(connectionString).Count);

        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Equal([(101L, 1L, 0, "re-announced")], ReadClients(connectionString));
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
            StateEvent(connectionString, Sender, 1002, version: 1, comment: "before"),
            StateEvent(connectionString, Sender, 2003, version: 1, comment: "not reached"));

        // act, the resync starts and re-announces one client, then the sender dies
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 2, comment: "re-announced"));

        // assert, the unreached client keeps its old state and the sender stays flagged
        Assert.Equal([(1002L, 2L, 0, "re-announced"), (2003L, 1L, 0, "not reached")], ReadClients(connectionString));
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

        // act, the trigger's INSERT
        InsertOneStatement(connectionString, DistinctiveStateEvent(connectionString, clientId: 101, seed: 1));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // act, the trigger's SET
        InsertOneStatement(connectionString, DistinctiveStateEvent(connectionString, clientId: 101, seed: 2));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // act, the reconcile's SET (101) and INSERT (201)
        Execute(connectionString, "DROP TRIGGER trg_project_clients ON client_events");

        InsertOneStatement(connectionString,
            DistinctiveStateEvent(connectionString, clientId: 101, seed: 3),
            DistinctiveStateEvent(connectionString, clientId: 201, seed: 3));

        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // and the rows compared were populated by the last write
        Assert.Equal(2, Scalar<long>(connectionString,
            "SELECT count(*) FROM clients WHERE comment LIKE '3-%' AND person_annual_income <> 0"));
    }

    [Fact(DisplayName = "Invariant Should name the log event that produced every state row")]
    public void Invariant_Should_name_the_log_event_that_produced_every_state_row()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 1));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 2));

        // assert, stamped with the newest state event
        const string notNewest = """
                                 SELECT c.client_id::text
                                 FROM clients c
                                          JOIN LATERAL (SELECT id
                                                        FROM client_events e
                                                        WHERE e.sender_id = c.sender_id
                                                          AND e.client_id = c.client_id
                                                          AND e.event_type = 0
                                                        ORDER BY e.id DESC
                                                        LIMIT 1) newest ON true
                                 WHERE c.source_event_row_id <> newest.id
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
            StateEvent(connectionString, Sender, 1002, version: 2, experience: 5, comment: "survives"),
            StateEvent(connectionString, Sender, 2003, version: 1, experience: 20, comment: "also survives"));

        // act, a purge is a DELETE; the trigger fires on INSERT only
        Execute(connectionString, "DELETE FROM client_events");

        // assert
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM client_events"));
        Assert.Equal([(1002L, 2L, 5, "survives"), (2003L, 1L, 20, "also survives")], ReadClients(connectionString));
    }

    /// <summary>
    /// The client API's phonePassword is out of the agreed schema; this keeps it out.
    /// </summary>
    [Fact(DisplayName = "Invariant Should have no column that could hold credential material")]
    public void Invariant_Should_have_no_column_that_could_hold_credential_material()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert, name and type both have to fit
        const string credentialColumns = """
                                         SELECT table_name || '.' || column_name
                                         FROM information_schema.columns
                                         WHERE table_schema = 'public'
                                           AND table_name IN ('client_events', 'clients')
                                           AND (column_name ILIKE '%password%' OR column_name ILIKE '%secret%'
                                                OR column_name ILIKE '%credential%')
                                           AND data_type IN ('character varying', 'character', 'text', 'bytea')
                                         """;

        Assert.Empty(Strings(connectionString, credentialColumns));
    }

    [Fact(DisplayName = "Invariant Should key the state table on sender and client")]
    public void Invariant_Should_key_the_state_table_on_sender_and_client()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        const string primaryKey = """
                                  SELECT string_agg(a.attname, ', ' ORDER BY k.ord)
                                  FROM pg_index i
                                           JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
                                           JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                                  WHERE i.indrelid = 'clients'::regclass
                                    AND i.indisprimary
                                  """;

        Assert.Equal("sender_id, client_id", Scalar<string>(connectionString, primaryKey));
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
        var row = StateEvent(connectionString, Sender, 1002, version: 1);

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // act
        using (var transaction = connection.BeginTransaction())
        {
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO client_events ({columns}) VALUES {row}";
                insert.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT count(*) FROM clients";

            // assert, visible before commit
            Assert.Equal(1L, (long)read.ExecuteScalar()!);

            transaction.Rollback();
        }

        // and gone after rollback
        Assert.Empty(ReadClients(connectionString));
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM client_events"));
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
            StateEvent(connectionString, Sender, 1002, version: 1, experience: 10, comment: "correct"));

        // right stamp, wrong content
        Execute(connectionString, "UPDATE clients SET experience_forex = 999, comment = 'corrupted'");
        Assert.Equal([(1002L, 1L, 999, "corrupted")], ReadClients(connectionString));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the log wins
        Assert.Equal([(1002L, 1L, 10, "correct")], ReadClients(connectionString));
    }

    /// <summary>
    /// The production path: the exporter over <c>COPY</c>, including a DELETE sweep.
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
            CopyLine(schema, Sender, eventType: 0, clientId: 1002, version: 1, experience: 10, comment: "live"),
            CopyLine(schema, Sender, eventType: 0, clientId: 2003, version: 1, experience: 20, comment: "swept"));

        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 2));

        // assert
        Assert.Equal([1002L, 2003L], ReadClients(request.ConnectionString).Select(client => client.ClientId));

        // the sweep needs DELETE as the exporter
        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 0, clientId: 1002, version: 1, experience: 10, comment: "live"));
        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 3));

        Assert.Equal([(1002L, 1L, 10, "live")], ReadClients(request.ConnectionString));

        // and ro reads but cannot write
        using var reader = new NpgsqlConnection(ReaderConnectionString(request));
        reader.Open();

        using (var read = reader.CreateCommand())
        {
            read.CommandText = "SELECT count(*) FROM clients";
            Assert.Equal(1L, (long)read.ExecuteScalar()!);
        }

        using (var read = reader.CreateCommand())
        {
            read.CommandText = $"SELECT resync_in_progress FROM clients_resync WHERE sender_id = {Sender}";
            Assert.False((bool)read.ExecuteScalar()!);
        }

        using var write = reader.CreateCommand();
        write.CommandText = "DELETE FROM clients";
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
        return Schema(connectionString, "client_events");
    }

    private static string Columns(string connectionString, string table)
    {
        return string.Join(", ", Schema(connectionString, table).Select(column => column.Column));
    }

    private static string EventColumns(string connectionString)
    {
        return Columns(connectionString, "client_events");
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
        return Row(connectionString, "client_events",
            [("sender_id", sender.ToString()), ("event_type", eventType.ToString()), .. overrides]);
    }

    /// <summary>
    /// A state event; the named fields stand in for the whole record.
    /// </summary>
    private static string StateEvent(string connectionString, long sender, long clientId, long version,
        long manager = 0, int experience = 0, string comment = "")
    {
        return Event(connectionString, sender, eventType: 0,
            ("client_id", clientId.ToString()),
            ("version", version.ToString()),
            ("assigned_manager", manager.ToString()),
            ("experience_forex", experience.ToString()),
            ("comment", Quote(comment)));
    }

    private static string DeleteEvent(string connectionString, long sender, long clientId)
    {
        return Event(connectionString, sender, eventType: 1, ("client_id", clientId.ToString()));
    }

    private static string Marker(string connectionString, long sender, int eventType)
    {
        return Event(connectionString, sender, eventType);
    }

    /// <summary>
    /// A full state event whose every column differs from every other; <paramref name="seed"/> changes them all.
    /// </summary>
    private static string DistinctiveStateEvent(string connectionString, long clientId, int seed)
    {
        var overrides = EventSchema(connectionString)
            .Where(column => column.Column is not ("sender_id" or "event_type" or "client_id"))
            .Select((column, ordinal) => (column.Column, Value: column.Type switch
            {
                // short enough for the narrowest column, VARCHAR(16)
                "character varying" => $"'{seed}-{ordinal}''s'",
                "timestamp with time zone" => $"to_timestamp({1700000000 + seed * 1000 + ordinal})",
                "double precision" => $"{seed * 1000 + ordinal}.25",
                _ => (seed * 1000 + ordinal + 1).ToString()
            }))
            .ToArray();

        return Event(connectionString, Sender, eventType: 0, [("client_id", clientId.ToString()), .. overrides]);
    }

    /// <summary>
    /// Columns where a state row differs from its source event, as <c>client_id: column</c>.
    /// </summary>
    private static List<string> Mismatches(string connectionString)
    {
        return Strings(connectionString,
            """
            SELECT c.client_id || ': ' || key
            FROM clients c
                     JOIN client_events e ON e.id = c.source_event_row_id
                     JOIN LATERAL jsonb_each(to_jsonb(e) - 'id' - 'time_inserted' - 'event_type') ON true
            WHERE value IS DISTINCT FROM ((to_jsonb(c) - 'source_event_row_id' - 'time_applied') -> key)
            ORDER BY c.client_id, key
            """);
    }

    private static string TradingAccountEvent(string connectionString, long sender, long tradingAccountId, long clientId, string owner)
    {
        return Row(connectionString, "trading_account_events",
            ("sender_id", sender.ToString()),
            ("event_type", "0"),
            ("trading_account_id", tradingAccountId.ToString()),
            ("version", "1"),
            ("client_id", clientId.ToString()),
            ("comment", Quote(owner)));
    }

    private static string Quote(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }

    /// <summary>
    /// One event as a <c>COPY</c> text line; empty strings, never <c>\N</c>, as every column is NOT NULL.
    /// </summary>
    private static string CopyLine(List<(string Column, string Type, bool Nullable)> schema, long sender, int eventType,
        long clientId = 0, long version = 0, int experience = 0, string comment = "")
    {
        return string.Join('\t', schema.Select(column => column.Column switch
        {
            "sender_id" => sender.ToString(),
            "event_type" => eventType.ToString(),
            "client_id" => clientId.ToString(),
            "version" => version.ToString(),
            "experience_forex" => experience.ToString(),
            "comment" => comment,
            _ => column.Type switch
            {
                "character varying" => "",
                "timestamp with time zone" => "1970-01-01 00:00:00+00",
                _ => "0"
            }
        }));
    }

    /// <summary>
    /// Sends rows the way the export does, over <c>COPY … FROM STDIN</c>.
    /// </summary>
    private static void Copy(NpgsqlConnection connection, string columns, params string[] rows)
    {
        using var writer = connection.BeginTextImport($"COPY client_events ({columns}) FROM STDIN");

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
            $"INSERT INTO client_events ({EventColumns(connectionString)}) VALUES {string.Join(", ", rows)}");
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

    private static List<(long ClientId, long Version, int Experience, string Comment)> ReadClients(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_id, version, experience_forex, comment FROM clients ORDER BY client_id, sender_id";

        using var reader = command.ExecuteReader();

        var clients = new List<(long, long, int, string)>();

        while (reader.Read())
        {
            clients.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return clients;
    }

    private static long ReadAssignedManager(string connectionString, long clientId)
    {
        return Scalar<long>(connectionString, $"SELECT assigned_manager FROM clients WHERE client_id = {clientId}");
    }

    private static string ReadComment(string connectionString, long sender, long clientId)
    {
        return Scalar<string>(connectionString,
            $"SELECT comment FROM clients WHERE sender_id = {sender} AND client_id = {clientId}");
    }

    private static bool ResyncInProgress(string connectionString, long sender)
    {
        return Scalar<bool>(connectionString, $"SELECT resync_in_progress FROM clients_resync WHERE sender_id = {sender}");
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

        var databaseName = $"client_projection_{Guid.NewGuid():N}"[..40];

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
