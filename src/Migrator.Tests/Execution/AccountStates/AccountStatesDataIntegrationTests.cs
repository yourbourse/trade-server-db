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

namespace YB.TradeServer.Db.Migrator.Tests.Execution.AccountStates;

/// <summary>
/// What ClickHouse does with the account_state_events change stream under the table's definition, and what the account_states
/// and account_states_live views make of it: snapshots and their watermark, versioning, deletes, markers and partitioning.
/// </summary>
/// <remarks>
/// Every test migrates with an hour-long refresh period and refreshes on demand, so no scheduled refresh can land
/// between the rows a test writes and the state it reads. Each state is asserted on both views.
/// </remarks>
[Trait("Category", "Integration")]
public class AccountStatesDataIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : AccountStatesTestBase(configBuilder, output)
{
    private string MigrateForState()
    {
        return Migrate($"account_states_refresh_seconds={NoScheduledRefresh}");
    }

    [Fact(DisplayName = "Workflow Should show the accounts of a completed snapshot")]
    public void Workflow_Should_show_the_accounts_of_a_completed_snapshot()
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

    [Fact(DisplayName = "Workflow Should show the latest change of an account, an account created after the snapshot, and not a deleted one")]
    public void Workflow_Should_show_the_latest_change_of_an_account_an_account_created_after_the_snapshot_and_not_a_deleted_one()
    {
        // arrange
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1, 2));

        // act, account 1 changes, 2 is deleted, 3 is created
        Insert(connectionString, Upsert(5, 1, "2.5"), Delete(6, 2), Upsert(7, 3, "0.1"));

        // assert
        AssertState(connectionString, ["7/1: 2.5", "7/3: 0.1"]);
    }

    /// <summary>
    /// An account the latest complete snapshot did not carry is hidden, deleted or not.
    /// </summary>
    [Fact(DisplayName = "Workflow Should hide the accounts missing from the latest completed snapshot")]
    public void Workflow_Should_hide_the_accounts_missing_from_the_latest_completed_snapshot()
    {
        // arrange, accounts 1 and 2, then account 3 created, all without a Delete since
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1, 2));
        Insert(connectionString, Upsert(5, 3));

        // act, a later snapshot carries account 1 alone
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

        // act, the next snapshot has carried account 1 and not yet account 2
        Insert(connectionString, SnapshotStart(5), Upsert(6, 1, "3"));

        // assert, account 2 stays, and account 1 shows its latest change
        AssertState(connectionString, ["7/1: 3", "7/2: 1"]);

        // and once the snapshot ends without account 2, it goes
        Insert(connectionString, SnapshotEnd(7));

        AssertState(connectionString, ["7/1: 3"]);
    }

    /// <summary>
    /// Each sender has its own watermark: one server's snapshot never hides another server's accounts.
    /// </summary>
    [Fact(DisplayName = "Workflow Should keep the accounts of two servers apart when their ids and sequences coincide")]
    public void Workflow_Should_keep_the_accounts_of_two_servers_apart_when_their_ids_and_sequences_coincide()
    {
        // arrange
        var connectionString = MigrateForState();

        // act, both servers snapshot the same account ids under the same numbering, then one snapshots again
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
    /// account_states is a table filled by the last refresh; account_states_live computes the state on every read.
    /// </summary>
    [Fact(DisplayName = "Workflow Should show a change in the live view at once and in account_states after the next refresh")]
    public void Workflow_Should_show_a_change_in_the_live_view_at_once_and_in_account_states_after_the_next_refresh()
    {
        // arrange
        var connectionString = MigrateForState();
        Insert(connectionString, Snapshot(1, Sender, 1));
        AssertState(connectionString, ["7/1: 1"]);

        // act
        Insert(connectionString, Upsert(4, 1, "2"));

        // assert
        Assert.Equal(["7/1: 2"], State(connectionString, "account_states_live"));
        Assert.Equal(["7/1: 1"], State(connectionString));

        AssertState(connectionString, ["7/1: 2"]);
    }

    /// <summary>
    /// The same rows arriving twice: the copy collapses when the parts merge, and FINAL hides it until then.
    /// </summary>
    [Fact(DisplayName = "Workflow Should show a re-sent batch twice until the parts merge, once under FINAL and once in the view")]
    public void Workflow_Should_show_a_re_sent_batch_twice_until_the_parts_merge_once_under_FINAL_and_once_in_the_view()
    {
        // arrange, merges held so that "before the merge" is not a race
        var connectionString = MigrateForState();
        Execute(connectionString, "SYSTEM STOP MERGES account_state_events");

        var batch = Snapshot(1, Sender, 1, 2);

        // act, the same rows arrive again in a later batch
        Insert(connectionString, batch);
        Insert(connectionString, [.. batch.Select(row => row with { BatchTime = HoursAgo(0) })]);

        // assert
        Assert.Equal(8, Count(connectionString, "account_state_events"));
        Assert.Equal(4, Count(connectionString, "account_state_events FINAL"));
        AssertState(connectionString, ["7/1: 1", "7/2: 1"]);

        // and the merge keeps one copy of each
        Execute(connectionString, "SYSTEM START MERGES account_state_events");
        Execute(connectionString, "OPTIMIZE TABLE account_state_events FINAL");

        Assert.Equal(4, Count(connectionString, "account_state_events"));
    }

    /// <summary>
    /// ReplacingMergeTree(sequence): the highest sequence wins, whatever order the rows arrive in.
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

        Execute(connectionString, "OPTIMIZE TABLE account_state_events FINAL");

        Assert.Equal(["3"], Strings(connectionString, "SELECT toString(balance) FROM account_state_events WHERE trading_account_id = 1"));
    }

    /// <summary>
    /// A late Upsert older than the Delete does not bring a deleted account back.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep an account deleted when an older change arrives after its Delete")]
    public void Invariant_Should_keep_an_account_deleted_when_an_older_change_arrives_after_its_Delete()
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
    /// Every marker has trading_account_id 0; marker_sequence keeps them apart under the sorting key, so none replaces another.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep every snapshot marker through a merge")]
    public void Invariant_Should_keep_every_snapshot_marker_through_a_merge()
    {
        // arrange
        var connectionString = MigrateForState();

        // act, two snapshots, merged
        Insert(connectionString, Snapshot(1, Sender, 1));
        Insert(connectionString, Snapshot(4, Sender, 1));
        Execute(connectionString, "OPTIMIZE TABLE account_state_events FINAL");

        // assert, four markers and the latest version of the one account
        Assert.Equal(["0: 1 SnapshotStart", "0: 3 SnapshotEnd", "0: 4 SnapshotStart", "0: 6 SnapshotEnd", "1: 5 Upsert"], Strings(connectionString,
            "SELECT concat(toString(trading_account_id), ': ', toString(sequence), ' ', toString(event_type)) FROM account_state_events ORDER BY trading_account_id, sequence"));
    }

    [Fact(DisplayName = "Invariant Should materialize marker_sequence as the sequence of a marker and 0 for an account")]
    public void Invariant_Should_materialize_marker_sequence_as_the_sequence_of_a_marker_and_0_for_an_account()
    {
        // arrange
        var connectionString = MigrateForState();

        // act
        Insert(connectionString, Snapshot(1, Sender, 1));

        // assert
        Assert.Equal(["1: 1", "2: 0", "3: 3"], Strings(connectionString,
            "SELECT concat(toString(sequence), ': ', toString(marker_sequence)) FROM account_state_events ORDER BY sequence"));
    }

    [Fact(DisplayName = "Invariant Should partition by sender")]
    public void Invariant_Should_partition_by_sender()
    {
        // arrange
        var connectionString = MigrateForState();

        // act
        Insert(connectionString, Upsert(1, 1), Upsert(1, 1, senderId: OtherSender));

        // assert
        Assert.Equal(["7", "8"], Strings(connectionString, "SELECT _partition_id FROM account_state_events ORDER BY sender_id"));
    }

    /// <summary>
    /// Rounding is the exporter's job: the server cuts the extra digits rather than rounding them.
    /// </summary>
    [Fact(DisplayName = "Edge case Should cut a balance beyond the column scale rather than round it")]
    public void Edge_case_Should_cut_a_balance_beyond_the_column_scale_rather_than_round_it()
    {
        // arrange
        var connectionString = MigrateForState();

        // act, one digit past Decimal(38, 12)
        Insert(connectionString, Upsert(1, 1, "1.9999999999999"));

        // assert
        Assert.Equal(["1.999999999999"], Strings(connectionString, "SELECT toString(balance) FROM account_state_events"));
    }

    /// <summary>
    /// One out-of-range value rejects the batch: nothing of it is stored, so the exporter keeps it and retries.
    /// </summary>
    [Fact(DisplayName = "Edge case Should refuse the whole batch when one balance overflows its column")]
    public void Edge_case_Should_refuse_the_whole_batch_when_one_balance_overflows_its_column()
    {
        // arrange
        var request = CreateRequest($"account_states_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        // act, assert, a balance needing 27 integer digits mid-batch
        AssertError(ExporterConnectionString(request),
            InsertSql(Upsert(1, 1), Upsert(2, 2, "100000000000000000000000000"), Upsert(3, 3)), ArgumentOutOfBound);

        Assert.Equal(0, Count(request.ConnectionString, "account_state_events"));
    }
}
