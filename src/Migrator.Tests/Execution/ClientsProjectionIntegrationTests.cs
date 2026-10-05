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
/// The same shape as <see cref="TradingAccountsProjectionIntegrationTests"/>, against the same projection
/// written for a different entity, and integration tests for the same reason: the projection is a plpgsql
/// trigger, so its behaviour - insertion order, ON CONFLICT, the resync sweep - is PostgreSQL's rather than this
/// repository's C#.
///
/// One thing is done differently, and the column count is why. A client carries sixty-five projected columns
/// against a trading account's fourteen, so the fixtures here build their rows from the live schema - every
/// column of the event table, in ordinal order, filled with a type-appropriate zero unless the test names it -
/// rather than from a positional list somebody has to keep in step. A column added to the log and forgotten
/// here would otherwise be sent as NULL and fail every test with a constraint violation pointing nowhere near
/// the cause.
///
/// Every test provisions its own database and drops it again on the way out, as MigrationIntegrationTests does.
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
    /// The trigger copies the client columns across by name, so the two tables have to keep the same set with
    /// the same types. A column added to one and not the other fails the migration; a type changed on one and
    /// not the other would not, and would silently truncate or round instead.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the same client columns in the log and the state table")]
    public void Schema_Should_carry_the_same_client_columns_in_the_log_and_the_state_table()
    {
        // arrange
        var connectionString = Migrate();

        // act, the columns each table holds that the other does not, ignoring the ones that are by design
        // local to one of them
        // format_type, not information_schema.data_type: the latter reports VARCHAR(32) and VARCHAR(512) alike
        // as "character varying", so a width that had drifted between the two tables would pass unnoticed
        const string sql = """
                           WITH cols AS (SELECT c.relname                            AS table_name,
                                                a.attname                            AS column_name,
                                                format_type(a.atttypid, a.atttypmod) AS column_type
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
    /// The reason the sweep runs at the end marker rather than the start: a resync spans several transactions,
    /// and the table has no way to hide a half-finished one from a reporting query.
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
    /// A regression test, carried over from the trading account projection this one was derived from. An AFTER
    /// ROW trigger fires once the whole statement has landed, so when a batch carries one resync's end marker
    /// followed by the next resync's start marker, every row of it - the later start marker included - is
    /// already in the table by the time the end marker's trigger runs. Reading the watermark as an unbounded
    /// max(id) therefore swept against the wrong resync and deleted everything the finishing one had just
    /// re-announced.
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

        // assert, both clients were re-announced under the resync that just finished, so both survive
        Assert.Equal([100L, 200L], ReadClients(connectionString).Select(client => client.ClientId));
    }

    [Fact(DisplayName = "Edge case Should store an unrecognised event type without projecting it")]
    public void Edge_case_Should_store_an_unrecognised_event_type_without_projecting_it()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 1, experience: 10));

        // act, an event type a later trade server might add. It must not raise: raising here would abort the
        // exporting COPY and stall the export of every deployment that had not migrated yet
        InsertOneStatement(connectionString,
            Event(connectionString, Sender, eventType: 99, ("client_id", "1002"), ("version", "7"), ("comment", "'unknown'")));

        // assert
        Assert.Equal(1, Scalar<long>(connectionString, "SELECT count(*) FROM client_events WHERE event_type = 99"));
        Assert.Equal([(1002L, 1L, 10, "")], ReadClients(connectionString));
    }

    /// <summary>
    /// There is no deduplication key on the log, so a redelivered event lands as a second row. The state table
    /// has to absorb that rather than double-apply it.
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
    /// Ids are reused after deletion, so a reused id restarts its version sequence. This is why the projection
    /// applies whatever arrives last instead of comparing versions: a guard would reject every event of the new
    /// client and leave the row showing the previous holder's data with nothing to heal it.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new client when an id is reused after deletion")]
    public void Edge_case_Should_show_the_new_client_when_an_id_is_reused_after_deletion()
    {
        // arrange
        var connectionString = Migrate();

        // act, the old client reaches version 3, is deleted, and the id is taken by a different person
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
    /// The same thing, but the deletion never reaches PostgreSQL: the sender was down while the old client was
    /// deleted and the id reused, so the reuse is first seen as a re-announcement inside a resync, at a lower
    /// version than the row already holds.
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

        // act, a complete resync for one sender that re-announces nothing at all
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert, the other sender's client is untouched
        Assert.Equal([5001L], ReadClients(connectionString).Select(client => client.ClientId));
    }

    /// <summary>
    /// The sender is part of the key, not merely a column: two trade servers exporting into one database may
    /// each hold a client under the same id, with different content. The composite primary key is what makes
    /// that structural, and every write path in the projection - the upsert, the deletion and the resync sweep -
    /// is scoped by sender_id.
    ///
    /// The test above uses two different client ids, so it would pass even if the key were client_id alone.
    /// This one would not.
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

        // assert, the other sender's client of the same id survives all of it, unchanged
        Assert.Equal(0, Scalar<long>(connectionString, $"SELECT count(*) FROM clients WHERE sender_id = {Sender}"));
        Assert.Equal([(1002L, 9L, 20, "sender 8")], ReadClients(connectionString));
    }

    /// <summary>
    /// Two trade server instances each number their own clients, so the same client_id on two senders is two
    /// unrelated people - often with different names. A reporting query therefore has to join on sender_id as
    /// well as client_id; one that does not silently attributes one instance's trading accounts to the other
    /// instance's client.
    ///
    /// This pins the rule stated in docs/SCHEMA_REFERENCE.md, "Every id is scoped to its sender". The wrong
    /// join is asserted too, so the test fails if it ever stops being wrong - which would mean the fixture no
    /// longer reproduces the collision it exists to describe.
    /// </summary>
    [Fact(DisplayName = "Invariant Should need sender_id as well as client_id to join clients to their accounts")]
    public void Invariant_Should_need_sender_id_as_well_as_client_id_to_join_clients_to_their_accounts()
    {
        // arrange, one client id used by two senders, with trading accounts on each
        var connectionString = Migrate();

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 1, comment: "Smith"),
            StateEvent(connectionString, OtherSender, 1002, version: 1, comment: "Jones"));

        Execute(connectionString,
            "INSERT INTO trading_account_events (sender_id, event_type, trading_account_id, version, group_id, " +
            "client_id, introducer, time_created, time_modified, time_password_last_changed, leverage, enabled, " +
            "allow_trading, external_account_id, comment) VALUES " +
            $"({Sender}, 0, 5001, 1, 1, 1002, 0, now(), now(), to_timestamp(0), 100, true, true, \'\', \'\'), " +
            $"({Sender}, 0, 5002, 1, 1, 1002, 0, now(), now(), to_timestamp(0), 100, true, true, \'\', \'\'), " +
            $"({OtherSender}, 0, 6001, 1, 1, 1002, 0, now(), now(), to_timestamp(0), 100, true, true, \'\', \'\')");

        // act, the full key against the id alone
        const string fullKey =
            "SELECT count(*)::text FROM clients c JOIN trading_accounts a " +
            "ON a.sender_id = c.sender_id AND a.client_id = c.client_id";

        const string idOnly =
            "SELECT count(*)::text FROM clients c JOIN trading_accounts a " +
            "ON a.client_id = c.client_id";

        // assert, three accounts belong to a client, and joining on the id alone invents three more
        Assert.Equal("3", Strings(connectionString, fullKey).Single());
        Assert.Equal("6", Strings(connectionString, idOnly).Single());

        // and each sender's client owns only its own accounts
        Assert.Equal(["5001", "5002"], Strings(connectionString,
            "SELECT a.trading_account_id::text FROM clients c JOIN trading_accounts a " +
            "ON a.sender_id = c.sender_id AND a.client_id = c.client_id " +
            $"WHERE c.sender_id = {Sender} ORDER BY a.trading_account_id"));

        Assert.Equal(["6001"], Strings(connectionString,
            "SELECT a.trading_account_id::text FROM clients c JOIN trading_accounts a " +
            "ON a.sender_id = c.sender_id AND a.client_id = c.client_id " +
            $"WHERE c.sender_id = {OtherSender} ORDER BY a.trading_account_id"));
    }

    /// <summary>
    /// A regression test for the reconcile pass. A sweep leaves no trace in the log - a client dropped from a
    /// resync is gone by its absence from the re-announcements, and absence is not an event - so a reconcile
    /// that only looked for the newest retained event per client read the swept client's last pre-resync state
    /// event and brought it back from the dead.
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
    /// The sender dies partway through a resync, so no end marker ever arrives and the sweep never runs.
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

        // assert, what it reached is current, what it did not keeps its previous state, and the sender stays
        // flagged so a reader can tell this picture is still converging
        Assert.Equal([(1002L, 2L, 0, "re-announced"), (2003L, 1L, 0, "not reached")], ReadClients(connectionString));
        Assert.True(ResyncInProgress(connectionString, Sender));
    }

    /// <summary>
    /// The projection copies; it does not interpret, convert or re-derive.
    ///
    /// This is the test that guards the column lists. The projection names sixty-five columns four times over -
    /// the VALUES list and the ON CONFLICT SET list in the function, then both again in the reconcile - and a
    /// mismapping between any two of them produces a row that is correctly stamped and quietly wrong. Rather
    /// than list the columns a fifth time here, the whole event row and the whole state row are compared as
    /// jsonb, so the assertion covers every column there is and names the ones that differ. Each column is given
    /// a value distinct from every other column's, so two columns crossed on the way across cannot compare
    /// equal, and the text ones carry a quote so a truncation or an escaping fault would show.
    /// </summary>
    [Fact(DisplayName = "Invariant Should copy every field from the event unchanged")]
    public void Invariant_Should_copy_every_field_from_the_event_unchanged()
    {
        // arrange
        var connectionString = Migrate();

        // act, one state event with every column distinctly populated
        InsertOneStatement(connectionString, DistinctiveStateEvent(connectionString, Sender, 4242));

        // assert, the state row and the event it names differ in no column either of them shares
        const string mismatches = """
                                  SELECT key
                                  FROM clients c
                                           JOIN client_events e ON e.id = c.source_event_row_id
                                           JOIN LATERAL jsonb_each(to_jsonb(e) - 'id' - 'time_inserted' - 'event_type') ON true
                                  WHERE value IS DISTINCT FROM
                                        ((to_jsonb(c) - 'source_event_row_id' - 'time_applied') -> key)
                                  """;

        Assert.Empty(Strings(connectionString, mismatches));

        // and the comparison above was actually made against a populated row, not an empty one that happens to
        // match an empty one
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM clients WHERE client_id = 4242 AND person_first_name <> '' AND person_annual_income <> 0"));
    }

    [Fact(DisplayName = "Invariant Should name the log event that produced every state row")]
    public void Invariant_Should_name_the_log_event_that_produced_every_state_row()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 1));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 1002, version: 2));

        // assert, the stamp is the newest state event for the client, not the one that created it
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
    /// The whole reason the current state is a table rather than a view over the log: a client can go unchanged
    /// for longer than the retention window, so the state has to outlive the events that built it.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep the current state when the log is purged")]
    public void Invariant_Should_keep_the_current_state_when_the_log_is_purged()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 1002, version: 2, experience: 5, comment: "survives"),
            StateEvent(connectionString, Sender, 2003, version: 1, experience: 20, comment: "also survives"));

        // act, a retention purge is a DELETE, and the projection fires on INSERT only
        Execute(connectionString, "DELETE FROM client_events");

        // assert
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM client_events"));
        Assert.Equal([(1002L, 2L, 5, "survives"), (2003L, 1L, 20, "also survives")], ReadClients(connectionString));
    }

    /// <summary>
    /// The agreed schema carries no credential material: phone_password was dropped from it, so the client
    /// tables hold nothing a reporting reader should not see. Enforced by the schema rather than promised by
    /// upstream code - there is no column for one, so one cannot arrive.
    /// </summary>
    [Fact(DisplayName = "Invariant Should have no column that could hold credential material")]
    public void Invariant_Should_have_no_column_that_could_hold_credential_material()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert, a column that could hold one: name and type both have to fit
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

        // assert, the primary key is what makes the invariant structural rather than incidental
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
    /// "No lag, no catching up": the state row is written by the same transaction as the event, so it is
    /// visible before that transaction commits and gone if it rolls back. That is what stops the two tables
    /// drifting, and it is the property a scheduled or polled projection could not offer.
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

            // assert, the writer sees the projected row before it commits
            Assert.Equal(1L, (long)read.ExecuteScalar()!);

            transaction.Rollback();
        }

        // and a batch that never commits leaves nothing behind in either table
        Assert.Empty(ReadClients(connectionString));
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM client_events"));
    }

    /// <summary>
    /// A row that is correctly stamped and wrong: what an earlier, buggy version of this projection would have
    /// left behind, since the function copies sixty-five columns twice and a mismapping produces exactly that.
    /// Fixing the function and re-applying the file has to correct such a row, which means the reconcile must
    /// replay the newest state event over it rather than compare the stamp against itself and skip.
    ///
    /// The UPDATE below is how the test manufactures that state, not a scenario in itself - nothing edits these
    /// tables by hand.
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

        // stand in for a row a broken projection wrote: right stamp, wrong content
        Execute(connectionString, "UPDATE clients SET experience_forex = 999, comment = 'corrupted'");
        Assert.Equal([(1002L, 1L, 999, "corrupted")], ReadClients(connectionString));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the log wins
        Assert.Equal([(1002L, 1L, 10, "correct")], ReadClients(connectionString));
    }

    /// <summary>
    /// Every other test here writes as the migration owner, which would keep passing if the grants were wrong or
    /// the invoker-rights trigger could not reach the derived table. This one runs the real production path:
    /// the exporter role, over <c>COPY</c>, including a resync whose sweep needs DELETE on a table the exporter
    /// never reads for itself.
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

        // act, two clients and a resync start, exactly as the export delivers them
        Copy(exporter, columns,
            CopyLine(schema, Sender, eventType: 0, clientId: 1002, version: 1, experience: 10, comment: "live"),
            CopyLine(schema, Sender, eventType: 0, clientId: 2003, version: 1, experience: 20, comment: "swept"));

        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 2));

        // assert, the trigger ran under the exporter's own rights and left the table complete
        Assert.Equal([1002L, 2003L], ReadClients(request.ConnectionString).Select(client => client.ClientId));

        // re-announce one client and close the resync: the sweep needs DELETE as the exporter
        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 0, clientId: 1002, version: 1, experience: 10, comment: "live"));
        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 3));

        Assert.Equal([(1002L, 1L, 10, "live")], ReadClients(request.ConnectionString));

        // and the reader role can read the projection but not write it
        using var reader = new NpgsqlConnection(ReaderConnectionString(request));
        reader.Open();

        using (var read = reader.CreateCommand())
        {
            read.CommandText = "SELECT count(*) FROM clients";
            Assert.Equal(1L, (long)read.ExecuteScalar()!);
        }

        using var write = reader.CreateCommand();
        write.CommandText = "DELETE FROM clients";
        var denied = Assert.Throws<PostgresException>(() => write.ExecuteNonQuery());
        Assert.Equal("42501", denied.SqlState);
    }

    // ---------------------------------------------------------------------------------------------------
    // Fixtures.
    //
    // Every row is built from the live schema rather than from a list kept here: the event table's columns in
    // ordinal order, each filled with the type-appropriate zero the exporter sends for a field that does not
    // apply, and then whatever the test names on top. Sixty-eight columns is too many to keep in step by hand,
    // and a forgotten one would arrive as NULL against a NOT NULL column and fail somewhere unhelpful.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The event table's columns, in ordinal order, excluding the two the table fills in itself.
    /// </summary>
    private static List<(string Column, string Type)> EventSchema(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT column_name, data_type
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = 'client_events'
                                AND column_name NOT IN ('id', 'time_inserted')
                              ORDER BY ordinal_position
                              """;

        using var reader = command.ExecuteReader();

        var columns = new List<(string, string)>();

        while (reader.Read())
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        return columns;
    }

    private static string EventColumns(string connectionString)
    {
        return string.Join(", ", EventSchema(connectionString).Select(column => column.Column));
    }

    /// <summary>
    /// The zero the exporter writes for a field that does not apply, as a SQL literal.
    /// </summary>
    private static string Zero(string dataType)
    {
        return dataType switch
        {
            "character varying" => "''",
            "timestamp with time zone" => "to_timestamp(0)",
            _ => "0"
        };
    }

    /// <summary>
    /// One event as a SQL tuple: every column of the log, zeroed, with the named ones overridden. The overrides
    /// carry SQL literals, so a string value arrives quoted.
    /// </summary>
    private static string Event(string connectionString, long sender, int eventType, params (string Column, string Value)[] overrides)
    {
        var values = EventSchema(connectionString).Select(column => column.Column switch
        {
            "sender_id" => sender.ToString(),
            "event_type" => eventType.ToString(),
            _ => overrides.FirstOrDefault(o => o.Column == column.Column).Value ?? Zero(column.Type)
        });

        return $"({string.Join(", ", values)})";
    }

    /// <summary>
    /// A state carrying event. The fields the tests vary stand in for the whole record: version for the
    /// client's own sequence, experience_forex for a number, comment for text, assigned_manager for a reference
    /// to something outside the client.
    /// </summary>
    private static string StateEvent(string connectionString, long sender, long clientId, long version,
        long manager = 1, int experience = 0, string comment = "")
    {
        return Event(connectionString, sender, eventType: 0,
            ("client_id", clientId.ToString()),
            ("version", version.ToString()),
            ("assigned_manager", manager.ToString()),
            ("experience_forex", experience.ToString()),
            ("comment", $"'{comment.Replace("'", "''")}'"),
            ("time_created", "now()"),
            ("time_modified", "now()"));
    }

    /// <summary>
    /// A deletion: it carries none of the client's state, so every field but the id is a zero.
    /// </summary>
    private static string DeleteEvent(string connectionString, long sender, long clientId)
    {
        return Event(connectionString, sender, eventType: 1, ("client_id", clientId.ToString()));
    }

    /// <summary>
    /// A resync marker: not about a client, so every field it does not use carries a zero, exactly as the
    /// exporter writes it.
    /// </summary>
    private static string Marker(string connectionString, long sender, int eventType)
    {
        return Event(connectionString, sender, eventType);
    }

    /// <summary>
    /// A state event whose every column holds a value no other column holds, so that two columns crossed on the
    /// way into the state table cannot compare equal. Text carries a quote and a column-specific suffix, numbers
    /// and timestamps a column-specific offset.
    /// </summary>
    private static string DistinctiveStateEvent(string connectionString, long sender, long clientId)
    {
        var schema = EventSchema(connectionString);

        var overrides = schema
            .Where(column => column.Column is not ("sender_id" or "event_type" or "client_id"))
            .Select((column, ordinal) => (column.Column, Value: column.Type switch
            {
                // the width is not known here, so the value is kept short enough for the narrowest column
                "character varying" => $"'{ordinal}''s'",
                "timestamp with time zone" => $"to_timestamp({1700000000 + ordinal})",
                "double precision" => $"{ordinal}.25",
                _ => (ordinal + 1).ToString()
            }))
            .ToArray();

        return Event(connectionString, sender, eventType: 0, [("client_id", clientId.ToString()), .. overrides]);
    }

    /// <summary>
    /// One event as a <c>COPY</c> text line: tab separated, in the schema's column order. An empty field is an
    /// empty string here, which is what the exporter has to send - a <c>\N</c> would be NULL and every column of
    /// this table is NOT NULL.
    /// </summary>
    private static string CopyLine(List<(string Column, string Type)> schema, long sender, int eventType,
        long clientId = 0, long version = 0, int experience = 0, string comment = "")
    {
        const string epoch = "1970-01-01 00:00:00+00";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff+00");
        var isState = eventType == 0;

        return string.Join('\t', schema.Select(column => column.Column switch
        {
            "sender_id" => sender.ToString(),
            "event_type" => eventType.ToString(),
            "client_id" => clientId.ToString(),
            "version" => version.ToString(),
            "experience_forex" => experience.ToString(),
            "comment" => comment,
            "time_created" or "time_modified" => isState ? now : epoch,
            _ => column.Type switch
            {
                "character varying" => "",
                "timestamp with time zone" => epoch,
                _ => "0"
            }
        }));
    }

    /// <summary>
    /// Sends rows the way the export does, over <c>COPY … FROM STDIN</c> rather than an INSERT.
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
    /// One INSERT carrying every row, so the whole set lands in a single statement and a single transaction -
    /// the shape a batch of the export arrives in. Called more than once, the rows arrive in separate
    /// transactions, which is how a resync spanning several of them is written.
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
        command.CommandText = "SELECT client_id, version, experience_forex, comment FROM clients ORDER BY client_id";

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

    /// <summary>
    /// Reads one client by its full key, which is the only way to tell two senders' rows apart when they share
    /// a client id.
    /// </summary>
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
    /// Provisions a database, migrates it, and returns its connection string.
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
            // the variables the CLI derives, so a variable added to the migrator reaches these tests without
            // anyone having to remember this file
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
