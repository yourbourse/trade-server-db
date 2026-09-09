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
/// The trigger that maintains the trading accounts table from the trading account event log.
/// </summary>
/// <remarks>
/// These are integration tests rather than unit tests because there is nothing to unit test: the projection is
/// a plpgsql trigger, and its behaviour - insertion order, ON CONFLICT, the resync sweep - is PostgreSQL's, not
/// this repository's C#. Both defects found in the projection so far were invisible to review and only appeared
/// when the trigger actually ran, which is what the resync and reuse cases below pin down.
///
/// Every test provisions its own database and drops it again on the way out, as MigrationIntegrationTests does.
/// </remarks>
[Trait("Category", "Integration")]
public class TradingAccountsProjectionIntegrationTests : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    private const long Sender = 7;
    private const long OtherSender = 8;

    /// <summary>
    /// The columns the trade server writes. The identity column and time_inserted are left to the table, as
    /// they are in the real export.
    /// </summary>
    private const string EventColumns =
        "sender_id, event_type, trading_account_id, version, group_id, client_id, introducer, " +
        "time_created, time_modified, time_password_last_changed, leverage, enabled, allow_trading, " +
        "external_account_id, comment";

    private readonly ConfigBuilder configBuilder;
    private readonly ITestOutputHelper output;
    private readonly List<(string ConnectionString, string DatabaseName)> provisionedDatabases = [];

    public TradingAccountsProjectionIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output)
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
        Assert.True(TableExists(connectionString, "trading_account_events"));
        Assert.True(TableExists(connectionString, "trading_accounts"));
        Assert.True(TableExists(connectionString, "trading_accounts_resync"));

        Assert.True(IndexExists(connectionString, "idx_account_row_trading_account_events"));
        Assert.True(IndexExists(connectionString, "idx_markers_trading_account_events"));

        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_project_trading_accounts' AND NOT tgisinternal"));
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_proc WHERE proname = 'project_trading_accounts'"));
    }

    /// <summary>
    /// The trigger copies the account columns across by name, so the two tables have to keep the same set with
    /// the same types. A column added to one and not the other fails the migration; a type changed on one and
    /// not the other would not, and would silently truncate or round instead.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the same account columns in the log and the state table")]
    public void Schema_Should_carry_the_same_account_columns_in_the_log_and_the_state_table()
    {
        // arrange
        var connectionString = Migrate();

        // act, the columns each table holds that the other does not, ignoring the ones that are by design
        // local to one of them
        // format_type, not information_schema.data_type: the latter reports both VARCHAR(64) and VARCHAR(256)
        // as "character varying", so a width that had drifted between the two tables would pass unnoticed
        const string sql = """
                           WITH cols AS (SELECT c.relname                                AS table_name,
                                                a.attname                                AS column_name,
                                                format_type(a.atttypid, a.atttypmod)     AS column_type
                                         FROM pg_attribute a
                                                  JOIN pg_class c ON c.oid = a.attrelid
                                                  JOIN pg_namespace n ON n.oid = c.relnamespace
                                         WHERE n.nspname = 'public'
                                           AND a.attnum > 0
                                           AND NOT a.attisdropped
                                           AND c.relname IN ('trading_account_events', 'trading_accounts'))
                           SELECT COALESCE(e.column_name, a.column_name) || ' (' ||
                                  COALESCE(e.column_type, 'absent') || ' / ' || COALESCE(a.column_type, 'absent') || ')'
                           FROM      (SELECT column_name, column_type FROM cols
                                      WHERE table_name = 'trading_account_events'
                                        AND column_name NOT IN ('id', 'time_inserted', 'event_type')) e
                           FULL JOIN (SELECT column_name, column_type FROM cols
                                      WHERE table_name = 'trading_accounts'
                                        AND column_name NOT IN ('source_event_row_id', 'time_applied')) a
                                  ON e.column_name = a.column_name
                           WHERE e.column_name IS NULL OR a.column_name IS NULL OR e.column_type <> a.column_type
                           """;

        // assert
        Assert.Empty(Strings(connectionString, sql));
    }

    [Fact(DisplayName = "Edge case Should let the last event of a batch win for one account")]
    public void Edge_case_Should_let_the_last_event_of_a_batch_win_for_one_account()
    {
        // arrange
        var connectionString = Migrate();

        // act, one statement carrying a creation, a modification and an unrelated account
        InsertOneStatement(connectionString,
            StateEvent(Sender, 1002, version: 1, leverage: 100, comment: "v1"),
            StateEvent(Sender, 2003, version: 1, leverage: 200, comment: "other"),
            StateEvent(Sender, 1002, version: 2, leverage: 50, comment: "v2"));

        // assert
        Assert.Equal([(1002L, 2L, 50, "v2"), (2003L, 1L, 200, "other")], ReadAccounts(connectionString));
    }

    [Fact(DisplayName = "Edge case Should never show an account created and deleted in one batch")]
    public void Edge_case_Should_never_show_an_account_created_and_deleted_in_one_batch()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString,
            StateEvent(Sender, 1002, version: 1),
            DeleteEvent(Sender, 1002));

        // assert
        Assert.Empty(ReadAccounts(connectionString));
    }

    [Fact(DisplayName = "Edge case Should ignore a deletion for an account with no row")]
    public void Edge_case_Should_ignore_a_deletion_for_an_account_with_no_row()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 1));

        // act
        InsertOneStatement(connectionString, DeleteEvent(Sender, 9999));

        // assert
        Assert.Single(ReadAccounts(connectionString));
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
            StateEvent(Sender, 1002, version: 1),
            StateEvent(Sender, 2003, version: 1));

        // act, the start marker arrives in a transaction of its own
        InsertOneStatement(connectionString, Marker(Sender, eventType: 2));

        // assert
        Assert.Equal(2, ReadAccounts(connectionString).Count);
        Assert.True(ResyncInProgress(connectionString, Sender));
    }

    [Fact(DisplayName = "Workflow Should converge the table on the live set a resync listed")]
    public void Workflow_Should_converge_the_table_on_the_live_set_a_resync_listed()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(Sender, 1002, version: 1),
            StateEvent(Sender, 2003, version: 1));

        // act, 2003 was deleted while the sender was down, so the resync re-announces only 1002
        InsertOneStatement(connectionString, Marker(Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 1));
        InsertOneStatement(connectionString, Marker(Sender, eventType: 3));

        // assert
        Assert.Equal([1002L], ReadAccounts(connectionString).Select(account => account.AccountId));
        Assert.False(ResyncInProgress(connectionString, Sender));
    }

    [Fact(DisplayName = "Edge case Should remove nothing for an end marker with no start marker")]
    public void Edge_case_Should_remove_nothing_for_an_end_marker_with_no_start_marker()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 1));

        // act
        InsertOneStatement(connectionString, Marker(Sender, eventType: 3));

        // assert
        Assert.Single(ReadAccounts(connectionString));
    }

    /// <summary>
    /// A regression test. An AFTER ROW trigger fires once the whole statement has landed, so when a batch
    /// carries one resync's end marker followed by the next resync's start marker, every row of it - the later
    /// start marker included - is already in the table by the time the end marker's trigger runs. Reading the
    /// watermark as an unbounded max(id) therefore swept against the wrong resync and deleted everything the
    /// finishing one had just re-announced.
    /// </summary>
    [Fact(DisplayName = "Regression Should sweep against its own start marker when a batch carries two resyncs")]
    public void Regression_Should_sweep_against_its_own_start_marker_when_a_batch_carries_two_resyncs()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, Marker(Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(Sender, 100, version: 1),
            StateEvent(Sender, 200, version: 1));

        // act, one statement ending the first resync and opening a second
        InsertOneStatement(connectionString,
            Marker(Sender, eventType: 3),
            Marker(Sender, eventType: 2));

        // assert, both accounts were re-announced under the resync that just finished, so both survive
        Assert.Equal([100L, 200L], ReadAccounts(connectionString).Select(account => account.AccountId));
    }

    [Fact(DisplayName = "Edge case Should store an unrecognised event type without projecting it")]
    public void Edge_case_Should_store_an_unrecognised_event_type_without_projecting_it()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 1, leverage: 100));

        // act, an event type a later trade server might add. It must not raise: raising here would abort the
        // exporting COPY and stall the export of every deployment that had not migrated yet
        InsertOneStatement(connectionString,
            $"({Sender}, 99, 1002, 7, 1, 1, 0, now(), now(), to_timestamp(0), 999, true, true, '', 'unknown')");

        // assert
        Assert.Equal(1, Scalar<long>(connectionString, "SELECT count(*) FROM trading_account_events WHERE event_type = 99"));
        Assert.Equal([(1002L, 1L, 100, "")], ReadAccounts(connectionString));
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
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 3, leverage: 25, comment: "v3"));
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 3, leverage: 25, comment: "v3"));

        // assert
        Assert.Equal(2, Scalar<long>(connectionString, "SELECT count(*) FROM trading_account_events"));
        Assert.Equal([(1002L, 3L, 25, "v3")], ReadAccounts(connectionString));
    }

    /// <summary>
    /// Ids are reused after deletion, so a reused id restarts its version sequence. This is why the projection
    /// applies whatever arrives last instead of comparing versions: a guard would reject every event of the new
    /// account and leave the row showing the previous holder's data with nothing to heal it.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new account when an id is reused after deletion")]
    public void Edge_case_Should_show_the_new_account_when_an_id_is_reused_after_deletion()
    {
        // arrange
        var connectionString = Migrate();

        // act, the old account reaches version 3, is deleted, and the id is taken by a different client
        InsertOneStatement(connectionString,
            StateEvent(Sender, 800, version: 1, clientId: 11, leverage: 100, comment: "old"),
            StateEvent(Sender, 800, version: 3, clientId: 11, leverage: 300, comment: "old"),
            DeleteEvent(Sender, 800),
            StateEvent(Sender, 800, version: 1, clientId: 99, leverage: 50, comment: "new"));

        // assert
        Assert.Equal([(800L, 1L, 50, "new")], ReadAccounts(connectionString));
        Assert.Equal(99, ReadClientId(connectionString, 800));
    }

    /// <summary>
    /// The same thing, but the deletion never reaches PostgreSQL: the sender was down while the old account was
    /// deleted and the id reused, so the reuse is first seen as a re-announcement inside a resync, at a lower
    /// version than the row already holds.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new account when a reused id is first seen in a resync")]
    public void Edge_case_Should_show_the_new_account_when_a_reused_id_is_first_seen_in_a_resync()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(Sender, 900, version: 4, clientId: 22, leverage: 400, comment: "old"));

        // act
        InsertOneStatement(connectionString, Marker(Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(Sender, 900, version: 1, clientId: 33, leverage: 25, comment: "new"));
        InsertOneStatement(connectionString, Marker(Sender, eventType: 3));

        // assert
        Assert.Equal([(900L, 1L, 25, "new")], ReadAccounts(connectionString));
        Assert.Equal(33, ReadClientId(connectionString, 900));
    }

    [Fact(DisplayName = "Edge case Should keep two senders in one database independent")]
    public void Edge_case_Should_keep_two_senders_in_one_database_independent()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(Sender, 1002, version: 1),
            StateEvent(OtherSender, 5001, version: 1));

        // act, a complete resync for one sender that re-announces nothing at all
        InsertOneStatement(connectionString, Marker(Sender, eventType: 2));
        InsertOneStatement(connectionString, Marker(Sender, eventType: 3));

        // assert, the other sender's account is untouched
        Assert.Equal([5001L], ReadAccounts(connectionString).Select(account => account.AccountId));
    }

    /// <summary>
    /// A regression test for the reconcile pass. A sweep leaves no trace in the log - an account dropped from a
    /// resync is gone by its absence from the re-announcements, and absence is not an event - so a reconcile
    /// that only looked for the newest retained event per account read the swept account's last pre-resync
    /// state event and brought it back from the dead.
    /// </summary>
    [Fact(DisplayName = "Regression Should not bring back a swept account when the projection runs again")]
    public void Regression_Should_not_bring_back_a_swept_account_when_the_projection_runs_again()
    {
        // arrange, 2003 is swept by a completed resync, leaving 1002 live
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(Sender, 1002, version: 1),
            StateEvent(Sender, 2003, version: 1));
        InsertOneStatement(connectionString, Marker(Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 1));
        InsertOneStatement(connectionString, Marker(Sender, eventType: 3));

        // and then the trigger is dropped by hand while the export carries on
        Execute(connectionString, "DROP TRIGGER trg_project_trading_accounts ON trading_account_events");
        InsertOneStatement(connectionString, StateEvent(Sender, 6001, version: 1, leverage: 400, comment: "while broken"));
        InsertOneStatement(connectionString, DeleteEvent(Sender, 1002));

        Assert.Equal([1002L], ReadAccounts(connectionString).Select(account => account.AccountId));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the drift is healed, the trigger is back, and the swept account stays swept
        Assert.Equal([(6001L, 1L, 400, "while broken")], ReadAccounts(connectionString));
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_project_trading_accounts' AND NOT tgisinternal"));
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
            StateEvent(Sender, 1002, version: 1, comment: "before"),
            StateEvent(Sender, 2003, version: 1, comment: "not reached"));

        // act, the resync starts and re-announces one account, then the sender dies
        InsertOneStatement(connectionString, Marker(Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 2, comment: "re-announced"));

        // assert, what it reached is current, what it did not keeps its previous state, and the sender stays
        // flagged so a reader can tell this picture is still converging
        Assert.Equal([(1002L, 2L, 100, "re-announced"), (2003L, 1L, 100, "not reached")], ReadAccounts(connectionString));
        Assert.True(ResyncInProgress(connectionString, Sender));
    }

    /// <summary>
    /// The projection copies; it does not interpret, convert or re-derive. Every column is given a distinctive
    /// value here, including a comment carrying a quote, so a conversion or a truncation anywhere would show.
    /// </summary>
    [Fact(DisplayName = "Invariant Should copy every field from the event unchanged")]
    public void Invariant_Should_copy_every_field_from_the_event_unchanged()
    {
        // arrange
        var connectionString = Migrate();

        // act
        Execute(connectionString,
            $@"INSERT INTO trading_account_events ({EventColumns}) VALUES
               ({Sender}, 0, 4242, 77, 88, 99, 1234,
                '2026-01-02 03:04:05.123456+00', '2026-02-03 04:05:06.654321+00', '2026-03-04 05:06:07.111111+00',
                33, false, true, 'EXT-4242', 'a comment with '' a quote')");

        // assert, no column of the state row differs from the event it was written from
        const string mismatches = """
                                  SELECT a.trading_account_id::text
                                  FROM trading_accounts a
                                           JOIN trading_account_events e ON e.id = a.source_event_row_id
                                  WHERE (a.version, a.group_id, a.client_id, a.leverage, a.introducer,
                                         a.external_account_id, a.comment, a.enabled, a.allow_trading,
                                         a.time_created, a.time_modified, a.time_password_last_changed)
                                            IS DISTINCT FROM
                                        (e.version, e.group_id, e.client_id, e.leverage, e.introducer,
                                         e.external_account_id, e.comment, e.enabled, e.allow_trading,
                                         e.time_created, e.time_modified, e.time_password_last_changed)
                                  """;

        Assert.Empty(Strings(connectionString, mismatches));

        // and the two booleans did not collapse into each other on the way across
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM trading_accounts WHERE enabled = false AND allow_trading = true"));
    }

    [Fact(DisplayName = "Invariant Should name the log event that produced every state row")]
    public void Invariant_Should_name_the_log_event_that_produced_every_state_row()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 1));
        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 2));

        // assert, the stamp is the newest state event for the account, not the one that created it
        const string notNewest = """
                                 SELECT a.trading_account_id::text
                                 FROM trading_accounts a
                                          JOIN LATERAL (SELECT id
                                                        FROM trading_account_events e
                                                        WHERE e.sender_id = a.sender_id
                                                          AND e.trading_account_id = a.trading_account_id
                                                          AND e.event_type = 0
                                                        ORDER BY e.id DESC
                                                        LIMIT 1) newest ON true
                                 WHERE a.source_event_row_id <> newest.id
                                 """;

        Assert.Empty(Strings(connectionString, notNewest));
    }

    /// <summary>
    /// The whole reason the current state is a table rather than a view over the log: an account can go
    /// unchanged for longer than the retention window, so the state has to outlive the events that built it.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep the current state when the log is purged")]
    public void Invariant_Should_keep_the_current_state_when_the_log_is_purged()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(Sender, 1002, version: 2, leverage: 50, comment: "survives"),
            StateEvent(Sender, 2003, version: 1, leverage: 200, comment: "also survives"));

        // act, a retention purge is a DELETE, and the projection fires on INSERT only
        Execute(connectionString, "DELETE FROM trading_account_events");

        // assert
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM trading_account_events"));
        Assert.Equal([(1002L, 2L, 50, "survives"), (2003L, 1L, 200, "also survives")], ReadAccounts(connectionString));
    }

    /// <summary>
    /// Enforced by the schema rather than promised by upstream code: there is no column for a password
    /// anywhere, so one cannot arrive even if the exporter's descriptor were widened by mistake.
    /// </summary>
    [Fact(DisplayName = "Invariant Should have no column that could hold credential material")]
    public void Invariant_Should_have_no_column_that_could_hold_credential_material()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert, a column that could hold one: name and type both have to fit. time_password_last_changed is
        // named for a password and holds a timestamp, which is why the type is part of the rule rather than a
        // list of exceptions that would need maintaining
        const string credentialColumns = """
                                         SELECT table_name || '.' || column_name
                                         FROM information_schema.columns
                                         WHERE table_schema = 'public'
                                           AND (column_name ILIKE '%password%' OR column_name ILIKE '%secret%'
                                                OR column_name ILIKE '%credential%')
                                           AND data_type IN ('character varying', 'character', 'text', 'bytea')
                                         """;

        Assert.Empty(Strings(connectionString, credentialColumns));
    }

    [Fact(DisplayName = "Invariant Should key the state table on sender and trading account")]
    public void Invariant_Should_key_the_state_table_on_sender_and_trading_account()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert, the primary key is what makes the invariant structural rather than incidental
        const string primaryKey = """
                                  SELECT string_agg(a.attname, ', ' ORDER BY k.ord)
                                  FROM pg_index i
                                           JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
                                           JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                                  WHERE i.indrelid = 'trading_accounts'::regclass
                                    AND i.indisprimary
                                  """;

        Assert.Equal("sender_id, trading_account_id", Scalar<string>(connectionString, primaryKey));
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

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // act
        using (var transaction = connection.BeginTransaction())
        {
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO trading_account_events ({EventColumns}) VALUES {StateEvent(Sender, 1002, version: 1)}";
                insert.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT count(*) FROM trading_accounts";

            // assert, the writer sees the projected row before it commits
            Assert.Equal(1L, (long)read.ExecuteScalar()!);

            transaction.Rollback();
        }

        // and a batch that never commits leaves nothing behind in either table
        Assert.Empty(ReadAccounts(connectionString));
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM trading_account_events"));
    }

    /// <summary>
    /// A row that is correctly stamped and wrong: what an earlier, buggy version of this projection would have
    /// left behind, since the function copies fourteen columns twice and a mismapping produces exactly that.
    /// Fixing the function and re-applying the file has to correct such a row, which means the reconcile must
    /// replay the newest state event over it rather than compare the stamp against itself and skip.
    ///
    /// The UPDATE below is how the test manufactures that state, not a scenario in itself - nothing edits these
    /// tables by hand, and SPEC.md assumes so.
    /// </summary>
    [Fact(DisplayName = "Regression Should correct a state row whose content disagrees with its event")]
    public void Regression_Should_correct_a_state_row_whose_content_disagrees_with_its_event()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString, StateEvent(Sender, 1002, version: 1, leverage: 100, comment: "correct"));

        // stand in for a row a broken projection wrote: right stamp, wrong content
        Execute(connectionString, "UPDATE trading_accounts SET leverage = 999, comment = 'corrupted'");
        Assert.Equal([(1002L, 1L, 999, "corrupted")], ReadAccounts(connectionString));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the log wins
        Assert.Equal([(1002L, 1L, 100, "correct")], ReadAccounts(connectionString));
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

        using var exporter = new NpgsqlConnection(ExporterConnectionString(request));
        exporter.Open();

        // act, two accounts and a resync start, exactly as the export delivers them
        Copy(exporter,
            EventRow(Sender, eventType: 0, accountId: 1002, version: 1, leverage: 100, comment: "live"),
            EventRow(Sender, eventType: 0, accountId: 2003, version: 1, leverage: 200, comment: "swept"));

        Copy(exporter, EventRow(Sender, eventType: 2));

        // assert, the trigger ran under the exporter's own rights and left the table complete
        Assert.Equal([1002L, 2003L], ReadAccounts(request.ConnectionString).Select(account => account.AccountId));

        // re-announce one account and close the resync: the sweep needs DELETE as the exporter
        Copy(exporter, EventRow(Sender, eventType: 0, accountId: 1002, version: 1, leverage: 100, comment: "live"));
        Copy(exporter, EventRow(Sender, eventType: 3));

        Assert.Equal([(1002L, 1L, 100, "live")], ReadAccounts(request.ConnectionString));

        // and the reader role can read the projection but not write it
        using var reader = new NpgsqlConnection(ReaderConnectionString(request));
        reader.Open();

        using (var read = reader.CreateCommand())
        {
            read.CommandText = "SELECT count(*) FROM trading_accounts";
            Assert.Equal(1L, (long)read.ExecuteScalar()!);
        }

        using var write = reader.CreateCommand();
        write.CommandText = "DELETE FROM trading_accounts";
        var denied = Assert.Throws<PostgresException>(() => write.ExecuteNonQuery());
        Assert.Equal("42501", denied.SqlState);
    }

    /// <summary>
    /// One event as a <c>COPY</c> text line: tab separated, in the order of <see cref="EventColumns"/>. An empty
    /// field is an empty string here, which is what the exporter has to send - a <c>\N</c> would be NULL and
    /// every column of this table is NOT NULL.
    /// </summary>
    private static string EventRow(long sender, int eventType, long accountId = 0, long version = 0,
        long clientId = 0, int leverage = 0, string comment = "")
    {
        const string epoch = "1970-01-01 00:00:00+00";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff+00");
        var isState = eventType == 0;

        return string.Join('\t', [
            sender.ToString(),
            eventType.ToString(),
            accountId.ToString(),
            version.ToString(),
            isState ? "1" : "0",
            clientId.ToString(),
            "0",
            isState ? now : epoch,
            isState ? now : epoch,
            epoch,
            leverage.ToString(),
            isState ? "true" : "false",
            isState ? "true" : "false",
            "",
            comment
        ]);
    }

    /// <summary>
    /// Sends rows the way the export does, over <c>COPY … FROM STDIN</c> rather than an INSERT.
    /// </summary>
    private static void Copy(NpgsqlConnection connection, params string[] rows)
    {
        using var writer = connection.BeginTextImport($"COPY trading_account_events ({EventColumns}) FROM STDIN");

        foreach (var row in rows)
        {
            writer.Write(row);
            writer.Write('\n');
        }
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

    private static string StateEvent(long sender, long accountId, long version, long clientId = 1, int leverage = 100, string comment = "")
    {
        return $"({sender}, 0, {accountId}, {version}, 1, {clientId}, 0, now(), now(), to_timestamp(0), {leverage}, true, true, '', '{comment}')";
    }

    private static string DeleteEvent(long sender, long accountId)
    {
        return $"({sender}, 1, {accountId}, 0, 0, 0, 0, to_timestamp(0), to_timestamp(0), to_timestamp(0), 0, false, false, '', '')";
    }

    /// <summary>
    /// A resync marker: not about an account, so every field it does not use carries a zero, exactly as the
    /// exporter writes it.
    /// </summary>
    private static string Marker(long sender, int eventType)
    {
        return $"({sender}, {eventType}, 0, 0, 0, 0, 0, to_timestamp(0), to_timestamp(0), to_timestamp(0), 0, false, false, '', '')";
    }

    /// <summary>
    /// One INSERT carrying every row, so the whole set lands in a single statement and a single transaction -
    /// the shape a batch of the export arrives in. Called more than once, the rows arrive in separate
    /// transactions, which is how a resync spanning several of them is written.
    /// </summary>
    private static void InsertOneStatement(string connectionString, params string[] rows)
    {
        Execute(connectionString, $"INSERT INTO trading_account_events ({EventColumns}) VALUES {string.Join(", ", rows)}");
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<(long AccountId, long Version, int Leverage, string Comment)> ReadAccounts(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT trading_account_id, version, leverage, comment FROM trading_accounts ORDER BY trading_account_id";

        using var reader = command.ExecuteReader();

        var accounts = new List<(long, long, int, string)>();

        while (reader.Read())
        {
            accounts.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return accounts;
    }

    private static long ReadClientId(string connectionString, long accountId)
    {
        return Scalar<long>(connectionString, $"SELECT client_id FROM trading_accounts WHERE trading_account_id = {accountId}");
    }

    private static bool ResyncInProgress(string connectionString, long sender)
    {
        return Scalar<bool>(connectionString, $"SELECT resync_in_progress FROM trading_accounts_resync WHERE sender_id = {sender}");
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

        var databaseName = $"trade_projection_{Guid.NewGuid():N}"[..40];

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
