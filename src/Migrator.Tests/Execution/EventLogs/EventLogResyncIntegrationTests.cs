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
/// How each derived table and its resync view move through a resync, in every shape the export can send one.
/// </summary>
[Trait("Category", "Integration")]
public class EventLogResyncIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    /// <summary>
    /// Why the sweep waits for the end marker: a resync spans several batches, and readers see each one.
    /// </summary>
    [Theory(DisplayName = "Workflow Should keep the table complete while a resync is running")]
    [EveryEventLog]
    public void Workflow_Should_keep_the_table_complete_while_a_resync_is_running(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1), State(2));

        // act
        Export(ResyncStarted());

        // assert
        Assert.Equal(["7/1 v1", "7/2 v1"], Rows());
        Assert.True(ResyncInProgress());
    }

    [Theory(DisplayName = "Workflow Should converge the table on the live set a resync listed")]
    [EveryEventLog]
    public void Workflow_Should_converge_the_table_on_the_live_set_a_resync_listed(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1), State(2));

        // act, 2 was deleted while the sender was down, so the resync re-announces 1 only
        Export(ResyncStarted());
        Export(State(1));
        Export(ResyncFinished());

        // assert
        Assert.Equal(["7/1 v1"], Rows());
        Assert.False(ResyncInProgress());
    }

    /// <summary>
    /// A change made between the markers is newer than the start marker, so the sweep keeps it.
    /// </summary>
    [Theory(DisplayName = "Workflow Should keep the changes exported while a resync is running")]
    [EveryEventLog]
    public void Workflow_Should_keep_the_changes_exported_while_a_resync_is_running(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1, label: "re-announced"), State(2, label: "live"), State(3, label: "live"), State(4, label: "gone while down"));

        // act, the resync re-announces 1 while 2 changes, 3 is deleted and 5 is created
        Export(ResyncStarted());
        Export(State(1, label: "re-announced"));
        Export(State(2, version: 2, label: "changed meanwhile"), Deleted(3), State(5, label: "created meanwhile"));
        Export(ResyncFinished());

        // assert, only 4, neither re-announced nor touched, is swept
        Assert.Equal(["7/1 v1 re-announced", "7/2 v2 changed meanwhile", "7/5 v1 created meanwhile"], Rows());
    }

    /// <summary>
    /// The sender dies partway, so no end marker arrives and nothing is swept.
    /// </summary>
    [Theory(DisplayName = "Workflow Should leave the table converging when a resync never finishes")]
    [EveryEventLog]
    public void Workflow_Should_leave_the_table_converging_when_a_resync_never_finishes(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1, label: "before"), State(2, label: "not reached"));

        // act, the resync re-announces 1, then the sender dies
        Export(ResyncStarted());
        Export(State(1, version: 2, label: "re-announced"));

        // assert, the sender stays flagged so a reader can tell the table is still converging
        Assert.Equal(["7/1 v2 re-announced", "7/2 v1 not reached"], Rows());
        Assert.True(ResyncInProgress());
    }

    /// <summary>
    /// The restarted resync lists everything live, so what only the abandoned one re-announced is gone.
    /// </summary>
    [Theory(DisplayName = "Workflow Should sweep against the newest start marker when a sender restarts mid-resync")]
    [EveryEventLog]
    public void Workflow_Should_sweep_against_the_newest_start_marker_when_a_sender_restarts_mid_resync(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1), State(2), State(3));

        // act, a resync re-announces 1, the sender restarts, and the new resync re-announces 2 and 3
        Export(ResyncStarted());
        Export(State(1, version: 2));

        var restarted = Export(ResyncStarted()).Single();
        Export(State(2, version: 2), State(3, version: 2));

        Assert.Equal(restarted, ResyncStatuses().Single().StartMarker);

        Export(ResyncFinished());

        // assert
        Assert.Equal(["7/2 v2", "7/3 v2"], Rows());
    }

    [Theory(DisplayName = "Workflow Should converge on a whole resync sent as one batch")]
    [EveryEventLog]
    public void Workflow_Should_converge_on_a_whole_resync_sent_as_one_batch(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1), State(2));

        // act, the start marker, the re-announcements and the end marker in one COPY
        Export(ResyncStarted(), State(2, version: 2), State(3), ResyncFinished());

        // assert
        Assert.Equal(["7/2 v2", "7/3 v1"], Rows());
        Assert.False(ResyncInProgress());
    }

    /// <summary>
    /// Sender 8 starts after sender 7 has re-announced, so reading the other sender's marker would sweep sender 7's row.
    /// </summary>
    [Theory(DisplayName = "Workflow Should keep the overlapping resyncs of two senders apart")]
    [EveryEventLog]
    public void Workflow_Should_keep_the_overlapping_resyncs_of_two_senders_apart(EventLog log)
    {
        // arrange, the same ids live on both senders
        Migrate(log);
        Export(State(1), State(2), State(1, sender: OtherSender), State(2, sender: OtherSender));

        // act, both resync at once, each re-announcing one id, their markers interleaved
        Export(ResyncStarted());
        Export(State(1, version: 2));
        Export(ResyncStarted(OtherSender));
        Export(State(2, version: 2, sender: OtherSender));
        Export(ResyncFinished(OtherSender));
        Export(ResyncFinished());

        // assert, each swept against its own start marker
        Assert.Equal(["7/1 v2", "8/2 v2"], Rows());
    }

    /// <summary>
    /// An AFTER ROW trigger sees the whole batch, so an unbounded lookup once read the next resync's start marker.
    /// </summary>
    [Theory(DisplayName = "Regression Should sweep against its own start marker when a batch carries two resyncs")]
    [EveryEventLog]
    public void Regression_Should_sweep_against_its_own_start_marker_when_a_batch_carries_two_resyncs(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(ResyncStarted());
        Export(State(1), State(2));

        // act, one batch ending the first resync and opening a second
        Export(ResyncFinished(), ResyncStarted());

        // assert, both were re-announced under the resync that finished, so both survive
        Assert.Equal(["7/1 v1", "7/2 v1"], Rows());
    }

    /// <summary>
    /// The deletion was missed while the sender was down, so the reuse first shows as a lower version in a resync.
    /// </summary>
    [Theory(DisplayName = "Edge case Should show the new entity when a reused id is first seen in a resync")]
    [EveryEventLog]
    public void Edge_case_Should_show_the_new_entity_when_a_reused_id_is_first_seen_in_a_resync(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(900, version: 4, label: "old"));

        // act
        Export(ResyncStarted());
        Export(State(900, version: 1, label: "new"));
        Export(ResyncFinished());

        // assert
        Assert.Equal(["7/900 v1 new"], Rows());
    }

    [Theory(DisplayName = "Edge case Should remove nothing for an end marker with no start marker, and not flag the sender")]
    [EveryEventLog]
    public void Edge_case_Should_remove_nothing_for_an_end_marker_with_no_start_marker_and_not_flag_the_sender(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1));

        // act
        var end = Export(ResyncFinished()).Single();

        // assert
        Assert.Equal(["7/1 v1"], Rows());
        Assert.Equal([new ResyncStatus(Sender, StartMarker: null, Started: null, ArrivalOf(end), InProgress: false)], ResyncStatuses());
    }

    /// <summary>
    /// A row outlives the events it came from, and its stamp still compares with a later start marker.
    /// </summary>
    [Theory(DisplayName = "Retention Should sweep an entity whose events have aged out when a resync leaves it out")]
    [EveryEventLog]
    public void Retention_Should_sweep_an_entity_whose_events_have_aged_out_when_a_resync_leaves_it_out(EventLog log)
    {
        // arrange, two live entities, then a purge of the whole log
        Migrate(log);
        Export(State(1), State(2));
        Execute($"DELETE FROM {Log.Events}");

        // act, a resync re-announces one of them
        Export(ResyncStarted());
        Export(State(1, version: 2));
        Export(ResyncFinished());

        // assert
        Assert.Equal(["7/1 v2"], Rows());
    }

    [Theory(DisplayName = "Resync view Should list only the senders that have resynced")]
    [EveryEventLog]
    public void Resync_view_Should_list_only_the_senders_that_have_resynced(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, sender 7 exports everything but a marker, sender 8 starts a resync
        Export(State(1), Deleted(1), Event(99));
        var start = Export(ResyncStarted(OtherSender)).Single();

        // assert
        Assert.Equal([new ResyncStatus(OtherSender, start, ArrivalOf(start), Finished: null, InProgress: true)], ResyncStatuses());
    }

    [Theory(DisplayName = "Resync view Should follow a sender through two resyncs")]
    [EveryEventLog]
    public void Resync_view_Should_follow_a_sender_through_two_resyncs(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, assert, the first resync starts
        var firstStart = Export(ResyncStarted()).Single();

        Assert.Equal([new ResyncStatus(Sender, firstStart, ArrivalOf(firstStart), Finished: null, InProgress: true)], ResyncStatuses());

        // and finishes
        var firstEnd = Export(ResyncFinished()).Single();

        Assert.Equal([new ResyncStatus(Sender, firstStart, ArrivalOf(firstStart), ArrivalOf(firstEnd), InProgress: false)], ResyncStatuses());

        // the second starts, and until it finishes the finish time is still the first one's
        var secondStart = Export(ResyncStarted()).Single();

        Assert.Equal([new ResyncStatus(Sender, secondStart, ArrivalOf(secondStart), ArrivalOf(firstEnd), InProgress: true)], ResyncStatuses());

        var secondEnd = Export(ResyncFinished()).Single();

        Assert.Equal([new ResyncStatus(Sender, secondStart, ArrivalOf(secondStart), ArrivalOf(secondEnd), InProgress: false)], ResyncStatuses());
    }

    /// <summary>
    /// Each sender runs its own export, so one can be mid-resync while the other has settled.
    /// </summary>
    [Theory(DisplayName = "Resync view Should report each sender on its own")]
    [EveryEventLog]
    public void Resync_view_Should_report_each_sender_on_its_own(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, sender 7 starts a resync, sender 8 runs a whole one
        var start = Export(ResyncStarted()).Single();
        var otherStart = Export(ResyncStarted(OtherSender)).Single();
        var otherEnd = Export(ResyncFinished(OtherSender)).Single();

        // assert
        Assert.Equal(
        [
            new ResyncStatus(Sender, start, ArrivalOf(start), Finished: null, InProgress: true),
            new ResyncStatus(OtherSender, otherStart, ArrivalOf(otherStart), ArrivalOf(otherEnd), InProgress: false)
        ], ResyncStatuses());
    }
}
