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

namespace YB.TradeServer.Db.Migrator.Tests.Execution.Positions;

/// <summary>
/// What ClickHouse does with the position_events change stream under the table's definition, and what the positions
/// and positions_live views make of it: snapshots and their watermark, versioning, deletes, markers and partitioning.
/// </summary>
/// <remarks>
/// Every test migrates with an hour-long refresh period and refreshes on demand, so no scheduled refresh can land
/// between the rows a test writes and the state it reads. Each state is asserted on both views.
/// </remarks>
[Trait("Category", "Integration")]
public class PositionsDataIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : PositionsTestBase(configBuilder, output)
{
    private string MigrateForState()
    {
        return Migrate($"positions_refresh_seconds={NoScheduledRefresh}");
    }

    [Fact(DisplayName = "Workflow Should show the positions of a completed snapshot")]
    public void Workflow_Should_show_the_positions_of_a_completed_snapshot()
    {
        // arrange
        var connectionString = MigrateForState();

        // act
        Insert(connectionString, Snapshot(1, Sender, 1, 2));

        // assert
        AssertState(connectionString, ["7/1: 1", "7/2: 1"]);
    }

    /// <summary>
    /// Without a SnapshotEnd there is no watermark, so the sender stays out of the view whatever it has sent.
    /// </summary>
    [Fact(DisplayName = "Workflow Should show nothing for a sender until its first snapshot completes")]
    public void Workflow_Should_show_nothing_for_a_sender_until_its_first_snapshot_completes()
    {
        // arrange
        var connectionString = MigrateForState();

        // act, the snapshot and a change after it, but no end marker yet
        Insert(connectionString, SnapshotStart(1), Upsert(2, 1), Upsert(3, 2));
        Insert(connectionString, Upsert(4, 1, "2"));

        // assert
        AssertState(connectionString, []);

        // and the end marker brings in everything from the start on
        Insert(connectionString, SnapshotEnd(5));

        AssertState(connectionString, ["7/1: 2", "7/2: 1"]);
    }

    [Fact(DisplayName = "Workflow Should show the latest change of a position, a position opened after the snapshot, and not a closed one")]
    public void Workflow_Should_show_the_latest_change_of_a_position_a_position_opened_after_the_snapshot_and_not_a_closed_one()
    {
        // arrange
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1, 2));

        // act, position 1 changes, 2 is closed, 3 is opened
        Insert(connectionString, Upsert(5, 1, "2.5"), Delete(6, 2), Upsert(7, 3, "0.1"));

        // assert
        AssertState(connectionString, ["7/1: 2.5", "7/3: 0.1"]);
    }

    /// <summary>
    /// A position the latest complete snapshot did not carry is one the server no longer knows, closed or not.
    /// </summary>
    [Fact(DisplayName = "Workflow Should hide the positions missing from the latest completed snapshot")]
    public void Workflow_Should_hide_the_positions_missing_from_the_latest_completed_snapshot()
    {
        // arrange, positions 1 and 2, then position 3 opened, all without a Delete since
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1, 2));
        Insert(connectionString, Upsert(5, 3));

        // act, a later snapshot - after a restart, say - knows position 1 alone
        Insert(connectionString, Snapshot(6, Sender, 1));

        // assert
        AssertState(connectionString, ["7/1: 1"]);
    }

    /// <summary>
    /// The watermark moves only when a SnapshotEnd follows the SnapshotStart; until then the previous one holds.
    /// </summary>
    [Fact(DisplayName = "Workflow Should keep showing the previous snapshot while the next one is in progress")]
    public void Workflow_Should_keep_showing_the_previous_snapshot_while_the_next_one_is_in_progress()
    {
        // arrange
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1, 2));

        // act, the next snapshot has re-sent position 1 and not yet position 2
        Insert(connectionString, SnapshotStart(5), Upsert(6, 1, "3"));

        // assert, position 2 stays, and position 1 shows its latest change
        AssertState(connectionString, ["7/1: 3", "7/2: 1"]);

        // and once the snapshot ends without position 2, it goes
        Insert(connectionString, SnapshotEnd(7));

        AssertState(connectionString, ["7/1: 3"]);
    }

    /// <summary>
    /// Each sender has its own watermark: one server's snapshot never hides another server's positions.
    /// </summary>
    [Fact(DisplayName = "Workflow Should keep the positions of two servers apart when their ids and sequences coincide")]
    public void Workflow_Should_keep_the_positions_of_two_servers_apart_when_their_ids_and_sequences_coincide()
    {
        // arrange
        var connectionString = MigrateForState();

        // act, both servers snapshot the same position ids under the same numbering, then one snapshots again
        Insert(connectionString, Snapshot(1, Sender, 1, 2));
        Insert(connectionString, Snapshot(1, OtherSender, 1, 2));
        Insert(connectionString, Snapshot(5, OtherSender, 2));

        // assert
        AssertState(connectionString, ["7/1: 1", "7/2: 1", "8/2: 1"]);
    }

    [Fact(DisplayName = "Workflow Should show only the senders whose snapshot has completed")]
    public void Workflow_Should_show_only_the_senders_whose_snapshot_has_completed()
    {
        // arrange
        var connectionString = MigrateForState();

        // act
        Insert(connectionString, Snapshot(1, Sender, 1));
        Insert(connectionString, SnapshotStart(1, OtherSender), Upsert(2, 1, senderId: OtherSender));

        // assert
        AssertState(connectionString, ["7/1: 1"]);
    }

    /// <summary>
    /// positions is a table filled by the last refresh; positions_live computes the state on every read.
    /// </summary>
    [Fact(DisplayName = "Workflow Should show a change in the live view at once and in positions after the next refresh")]
    public void Workflow_Should_show_a_change_in_the_live_view_at_once_and_in_positions_after_the_next_refresh()
    {
        // arrange
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1));
        AssertState(connectionString, ["7/1: 1"]);

        // act
        Insert(connectionString, Upsert(4, 1, "2"));

        // assert
        Assert.Equal(["7/1: 2"], State(connectionString, "positions_live"));
        Assert.Equal(["7/1: 1"], State(connectionString));

        AssertState(connectionString, ["7/1: 2"]);
    }

    /// <summary>
    /// At-least-once delivery: the copy collapses when the parts merge, and FINAL hides it until then.
    /// </summary>
    [Fact(DisplayName = "Workflow Should show a re-sent batch twice until the parts merge, once under FINAL and once in the view")]
    public void Workflow_Should_show_a_re_sent_batch_twice_until_the_parts_merge_once_under_FINAL_and_once_in_the_view()
    {
        // arrange, merges held so that "before the merge" is not a race
        var connectionString = MigrateForState();
        Execute(connectionString, "SYSTEM STOP MERGES position_events");

        var batch = Snapshot(1, Sender, 1, 2);

        // act, the confirmation is lost and the rows go again in a later batch
        Insert(connectionString, batch);
        Insert(connectionString, [.. batch.Select(row => row with { BatchTime = HoursAgo(0) })]);

        // assert
        Assert.Equal(8, Count(connectionString, "position_events"));
        Assert.Equal(4, Count(connectionString, "position_events FINAL"));
        AssertState(connectionString, ["7/1: 1", "7/2: 1"]);

        // and the merge keeps one copy of each
        Execute(connectionString, "SYSTEM START MERGES position_events");
        Execute(connectionString, "OPTIMIZE TABLE position_events FINAL");

        Assert.Equal(4, Count(connectionString, "position_events"));
    }

    /// <summary>
    /// ReplacingMergeTree(sequence): the highest sequence wins, so a late batch never overwrites a newer change.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep the change with the highest sequence whatever the order it arrives in")]
    public void Invariant_Should_keep_the_change_with_the_highest_sequence_whatever_the_order_it_arrives_in()
    {
        // arrange
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1));

        // act, the newer change first, the older one after it
        Insert(connectionString, Upsert(5, 1, "3"));
        Insert(connectionString, Upsert(4, 1, "2"));

        // assert
        AssertState(connectionString, ["7/1: 3"]);

        Execute(connectionString, "OPTIMIZE TABLE position_events FINAL");

        Assert.Equal(["3"], Strings(connectionString, "SELECT toString(volume) FROM position_events WHERE position_id = 1"));
    }

    /// <summary>
    /// A late Upsert older than the Delete does not bring a closed position back.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep a position closed when an older change arrives after its Delete")]
    public void Invariant_Should_keep_a_position_closed_when_an_older_change_arrives_after_its_Delete()
    {
        // arrange
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1, 2));

        // act
        Insert(connectionString, Delete(6, 2));
        Insert(connectionString, Upsert(5, 2, "9"));

        // assert
        AssertState(connectionString, ["7/1: 1"]);
    }

    /// <summary>
    /// Every marker has position_id 0; marker_sequence keeps them apart under the sorting key, so none replaces another.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep every snapshot marker through a merge")]
    public void Invariant_Should_keep_every_snapshot_marker_through_a_merge()
    {
        // arrange
        var connectionString = MigrateForState();

        // act, two snapshots, merged
        Insert(connectionString, Snapshot(1, Sender, 1));
        Insert(connectionString, Snapshot(4, Sender, 1));
        Execute(connectionString, "OPTIMIZE TABLE position_events FINAL");

        // assert, four markers and the latest version of the one position
        Assert.Equal(["0: 1 SnapshotStart", "0: 3 SnapshotEnd", "0: 4 SnapshotStart", "0: 6 SnapshotEnd", "1: 5 Upsert"], Strings(connectionString,
            "SELECT concat(toString(position_id), ': ', toString(sequence), ' ', toString(event_type)) FROM position_events ORDER BY position_id, sequence"));
    }

    [Fact(DisplayName = "Invariant Should materialize marker_sequence as the sequence of a marker and 0 for a position")]
    public void Invariant_Should_materialize_marker_sequence_as_the_sequence_of_a_marker_and_0_for_a_position()
    {
        // arrange
        var connectionString = MigrateForState();

        // act
        Insert(connectionString, Snapshot(1, Sender, 1));

        // assert
        Assert.Equal(["1: 1", "2: 0", "3: 3"], Strings(connectionString,
            "SELECT concat(toString(sequence), ': ', toString(marker_sequence)) FROM position_events ORDER BY sequence"));
    }

    [Fact(DisplayName = "Invariant Should partition by sender")]
    public void Invariant_Should_partition_by_sender()
    {
        // arrange
        var connectionString = MigrateForState();

        // act
        Insert(connectionString, Upsert(1, 1), Upsert(1, 1, senderId: OtherSender));

        // assert
        Assert.Equal(["7", "8"], Strings(connectionString, "SELECT _partition_id FROM position_events ORDER BY sender_id"));
    }

    /// <summary>
    /// Rounding is the exporter's job: the server cuts the extra digits rather than rounding them.
    /// </summary>
    [Fact(DisplayName = "Edge case Should cut a volume beyond the column scale rather than round it")]
    public void Edge_case_Should_cut_a_volume_beyond_the_column_scale_rather_than_round_it()
    {
        // arrange
        var connectionString = MigrateForState();

        // act, one digit past Decimal(18, 8)
        Insert(connectionString, Upsert(1, 1, "1.999999999"));

        // assert
        Assert.Equal(["1.99999999"], Strings(connectionString, "SELECT toString(volume) FROM position_events"));
    }

    /// <summary>
    /// One out-of-range value rejects the batch: nothing of it is stored, so the exporter keeps it and retries.
    /// </summary>
    [Fact(DisplayName = "Edge case Should refuse the whole batch when one volume overflows its column")]
    public void Edge_case_Should_refuse_the_whole_batch_when_one_volume_overflows_its_column()
    {
        // arrange
        var request = CreateRequest($"positions_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        // act, assert, a volume needing 11 integer digits mid-batch
        AssertError(ExporterConnectionString(request),
            InsertSql(Upsert(1, 1), Upsert(2, 2, "10000000000"), Upsert(3, 3)), ArgumentOutOfBound);

        Assert.Equal(0, Count(request.ConnectionString, "position_events"));
    }
}
