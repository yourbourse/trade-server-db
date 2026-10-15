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
/// How each derived table follows its log, entity by entity: states, deletions, ids that recur, stamps and full rows.
/// </summary>
[Trait("Category", "Integration")]
public class EventLogProjectionIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    /// <summary>
    /// Boolean b carries bit p of b + 1 in event p, so any two of up to 31 booleans differ in some event.
    /// </summary>
    private const int BooleanPatterns = 5;

    [Theory(DisplayName = "Workflow Should follow an entity through creation, change, deletion and re-creation")]
    [EveryEventLog]
    public void Workflow_Should_follow_an_entity_through_creation_change_deletion_and_re_creation(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, assert, one batch at a time
        Export(State(1, label: "created"));
        Assert.Equal(["7/1 v1 created"], Rows());

        Export(State(1, version: 2, label: "changed"));
        Assert.Equal(["7/1 v2 changed"], Rows());

        Export(Deleted(1));
        Assert.Empty(Rows());

        Export(State(1, label: "created again"));
        Assert.Equal(["7/1 v1 created again"], Rows());
    }

    [Theory(DisplayName = "Edge case Should let the last event of a batch win for one entity")]
    [EveryEventLog]
    public void Edge_case_Should_let_the_last_event_of_a_batch_win_for_one_entity(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, one batch carrying a creation, an unrelated entity and a change to the first
        Export(State(1, label: "created"), State(2, label: "other"), State(1, version: 2, label: "changed"));

        // assert
        Assert.Equal(["7/1 v2 changed", "7/2 v1 other"], Rows());
    }

    [Theory(DisplayName = "Edge case Should never show an entity created and deleted in one batch")]
    [EveryEventLog]
    public void Edge_case_Should_never_show_an_entity_created_and_deleted_in_one_batch(EventLog log)
    {
        // arrange
        Migrate(log);

        // act
        Export(State(1), Deleted(1));

        // assert
        Assert.Empty(Rows());
    }

    [Theory(DisplayName = "Edge case Should ignore a deletion for an entity with no row")]
    [EveryEventLog]
    public void Edge_case_Should_ignore_a_deletion_for_an_entity_with_no_row(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1));

        // act
        Export(Deleted(9999));

        // assert
        Assert.Equal(["7/1 v1"], Rows());
    }

    /// <summary>
    /// Raising on a type a later trade server adds would abort the exporting COPY and stall the export.
    /// </summary>
    [Theory(DisplayName = "Edge case Should store an event type the projection does not know, without projecting it")]
    [EveryEventLog]
    public void Edge_case_Should_store_an_event_type_the_projection_does_not_know_without_projecting_it(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1, label: "known"));

        // act
        Export(Event(99).With(Log.IdColumn, 1L).With("version", 7L).With(Log.LabelColumn, "unknown"));

        // assert
        Assert.Equal(["state 1", "type 99"], EventsInLogOrder());
        Assert.Equal(["7/1 v1 known"], Rows());
    }

    /// <summary>
    /// The log has no deduplication key, so the derived table has to absorb an at-least-once delivery.
    /// </summary>
    [Theory(DisplayName = "Edge case Should leave the same state when an event is redelivered")]
    [EveryEventLog]
    public void Edge_case_Should_leave_the_same_state_when_an_event_is_redelivered(EventLog log)
    {
        // arrange
        Migrate(log);

        // act
        Export(State(1, version: 3, label: "sent twice"));
        Export(State(1, version: 3, label: "sent twice"));

        // assert
        Assert.Equal(["state 1", "state 1"], EventsInLogOrder());
        Assert.Equal(["7/1 v3 sent twice"], Rows());
    }

    /// <summary>
    /// A reused id starts its versions again, which is why the projection orders by arrival and never by version.
    /// </summary>
    [Theory(DisplayName = "Edge case Should show the new entity when an id is reused after deletion")]
    [EveryEventLog]
    public void Edge_case_Should_show_the_new_entity_when_an_id_is_reused_after_deletion(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, the old entity reaches version 3 and is deleted, and a new one takes its id
        Export(State(800, version: 1, label: "old"), State(800, version: 3, label: "old"), Deleted(800), State(800, version: 1, label: "new"));

        // assert
        Assert.Equal(["7/800 v1 new"], Rows());
    }

    /// <summary>
    /// Each sender numbers its own entities, so every write path has to key on the sender as well as the id.
    /// </summary>
    [Theory(DisplayName = "Edge case Should keep the same id on two senders apart")]
    [EveryEventLog]
    public void Edge_case_Should_keep_the_same_id_on_two_senders_apart(EventLog log)
    {
        // arrange, one id on two senders
        Migrate(log);
        Export(State(1, label: "sender 7"), State(1, version: 9, label: "sender 8", sender: OtherSender));

        // act, sender 7 changes it, deletes it, and runs a resync that re-announces nothing
        Export(State(1, version: 2, label: "sender 7 changed"));
        Export(Deleted(1));
        Export(ResyncStarted());
        Export(ResyncFinished());

        // assert
        Assert.Equal(["8/1 v9 sender 8"], Rows());
    }

    /// <summary>
    /// The trigger writes in the batch's transaction, so its now() is the time_inserted of the event it applies.
    /// </summary>
    [Theory(DisplayName = "Invariant Should stamp each row with the event that last wrote it and with its arrival time")]
    [EveryEventLog]
    public void Invariant_Should_stamp_each_row_with_the_event_that_last_wrote_it_and_with_its_arrival_time(EventLog log)
    {
        // arrange
        Migrate(log);
        var first = Export(State(1), State(2));

        // act, a later batch changes one of the two
        var change = Export(State(2, version: 2)).Single();

        // assert
        Assert.Equal([$"7/1 v1 #{first[0]}", $"7/2 v2 #{change}"], RowsWithStamps());

        Assert.Equal(ArrivalOf(first[0]), Scalar<DateTime>($"SELECT time_applied FROM {Log.State} WHERE {Log.IdColumn} = 1"));
        Assert.Equal(ArrivalOf(change), Scalar<DateTime>($"SELECT time_applied FROM {Log.State} WHERE {Log.IdColumn} = 2"));
    }

    /// <summary>
    /// Every column is named four times; values distinct per column and per write show one crossed or forgotten anywhere.
    /// </summary>
    [Theory(DisplayName = "Invariant Should copy every field unchanged on all four write paths")]
    [EveryEventLog]
    public void Invariant_Should_copy_every_field_unchanged_on_all_four_write_paths(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, assert, the INSERT of the trigger
        Export(Distinctive(firstId: 1, write: 1));
        Assert.Empty(Mismatches());

        // its SET
        Export(Distinctive(firstId: 1, write: 2));
        Assert.Empty(Mismatches());

        // the SET (ids 1 to 5) and the INSERT (ids 11 to 15) of the reconcile
        DropTrigger();
        Export([.. Distinctive(firstId: 1, write: 3), .. Distinctive(firstId: 11, write: 4)]);
        MigrateAgain(force: true);

        Assert.Empty(Mismatches());

        // and what was compared is the last write of each
        Assert.Equal(2 * BooleanPatterns, Scalar<long>($"SELECT count(*) FROM {Log.State} WHERE version IN (3, 4)"));
    }

    /// <summary>
    /// Why the derived table is a table: an entity can outlive the retention of the events that built it.
    /// </summary>
    [Theory(DisplayName = "Retention Should keep the derived table when the log is purged")]
    [EveryEventLog]
    public void Retention_Should_keep_the_derived_table_when_the_log_is_purged(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1, version: 2, label: "survives"), State(2, label: "also survives"));

        // act, a purge is a DELETE, and the trigger fires on INSERT only
        Execute($"DELETE FROM {Log.Events}");

        // assert
        Assert.Equal(0, Count(Log.Events));
        Assert.Equal(["7/1 v2 survives", "7/2 v1 also survives"], Rows());
    }

    /// <summary>
    /// One state event per boolean pattern, every column holding a value no other column and no other write holds.
    /// </summary>
    /// <remarks>
    /// An even write inverts every boolean and moves the NULLs of a nullable column to the other events.
    /// </remarks>
    private LogEvent[] Distinctive(long firstId, int write)
    {
        var columns = LogColumns.Where(column => column.Name is not ("sender_id" or "event_type") && column.Name != Log.IdColumn).ToList();
        var booleans = columns.Where(column => column.Type == "boolean").Select(column => column.Name).ToList();
        var even = write % 2 == 0;

        Assert.True(booleans.Count < 1 << BooleanPatterns, $"More booleans than {BooleanPatterns} patterns tell apart; raise BooleanPatterns.");

        return
        [
            .. Enumerable.Range(0, BooleanPatterns).Select(pattern => columns
                .Select((column, ordinal) => (column, ordinal))
                .Aggregate(State(firstId + pattern, version: write), (state, next) => next.column.Name == "version"
                    ? state
                    : state.With(next.column.Name, next.column switch
                    {
                        { Nullable: true } when pattern % 2 == 1 != even => null,
                        { Type: "character varying" } => $"{write}-{next.ordinal}'s",
                        { Type: "timestamp with time zone" } => $"2026-{write:00}-01 00:00:00.{next.ordinal:000000}+00",
                        { Type: "numeric" } => $"{write * 1000 + next.ordinal}.000000000000000001",
                        { Type: "double precision" } => $"{write * 1000 + next.ordinal}.25",
                        { Type: "boolean" } => (((booleans.IndexOf(next.column.Name) + 1) >> pattern) & 1) == 1 != even,
                        _ => write * 1000 + next.ordinal + 1
                    })))
        ];
    }
}
