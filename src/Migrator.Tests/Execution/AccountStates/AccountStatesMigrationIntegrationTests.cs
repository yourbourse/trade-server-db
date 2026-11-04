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
/// What the migration makes of the account_state_events table and the account_states and account_states_live views: their
/// definitions, re-runs, the refresh period, retention and grants.
/// </summary>
[Trait("Category", "Integration")]
public class AccountStatesMigrationIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : AccountStatesTestBase(configBuilder, output)
{
    /// <summary>
    /// SHOW CREATE TABLE in full: fails on any change to a column, type, key, TTL or setting.
    /// </summary>
    [Fact(DisplayName = "Schema Should create the account_state_events table with the agreed columns, engine, keys and retention")]
    public void Schema_Should_create_the_account_state_events_table_with_the_agreed_columns_engine_keys_and_retention()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.Equal(AgreedTable(), ShowCreate(connectionString, "account_state_events"));
    }

    [Fact(DisplayName = "Schema Should create the account_states view refreshed every 5 minutes into a MergeTree")]
    public void Schema_Should_create_the_account_states_view_refreshed_every_5_minutes_into_a_MergeTree()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        var view = ShowCreate(connectionString, "account_states");

        Assert.StartsWith("CREATE MATERIALIZED VIEW account_states", view);
        Assert.Contains("REFRESH EVERY 5 MINUTE", view);
        Assert.Contains("ENGINE = MergeTree", view);
        Assert.Contains("ORDER BY (sender_id, trading_account_id)", view);
        Assert.Contains("FROM account_states_live", view);
    }

    /// <summary>
    /// The one definition of the state: a plain view reading account_state_events with FINAL, as the reader who queries it.
    /// </summary>
    [Fact(DisplayName = "Schema Should create the account_states_live view over account_state_events with FINAL")]
    public void Schema_Should_create_the_account_states_live_view_over_account_state_events_with_FINAL()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        var view = ShowCreate(connectionString, "account_states_live");

        Assert.StartsWith("CREATE VIEW account_states_live", view);
        Assert.DoesNotContain("DEFINER", view); // the default, INVOKER, is not printed
        Assert.Contains("FROM account_state_events AS account_state_events\nFINAL", view);
        Assert.Equal("View", Scalar<string>(connectionString,
            "SELECT engine FROM system.tables WHERE database = currentDatabase() AND name = 'account_states_live'"));
    }

    /// <summary>
    /// Both views expose every column the exporter writes, in the table's order, and not marker_sequence.
    /// </summary>
    [Theory(DisplayName = "Schema Should give an account_states view every exported column and no marker_sequence")]
    [InlineData("account_states")]
    [InlineData("account_states_live")]
    public void Schema_Should_give_an_account_states_view_every_exported_column_and_no_marker_sequence(string view)
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.Equal(AccountStateColumns.Split(", "), Strings(connectionString,
            $"SELECT name FROM system.columns WHERE database = currentDatabase() AND table = '{view}' ORDER BY position"));
    }

    /// <summary>
    /// views/ re-runs a changed script; CREATE OR REPLACE swaps the live view's query, and account_states follows it.
    /// </summary>
    [Fact(DisplayName = "Migration Should replace the live view and keep account_states reading it when the live script re-runs")]
    public void Migration_Should_replace_the_live_view_and_keep_account_states_reading_it_when_the_live_script_re_runs()
    {
        // arrange
        var request = CreateRequest($"account_states_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1));
        AssertState(request.ConnectionString, ["7/1: 1"]);

        // act, the views phase forced, as a changed script would re-run it
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));
        Insert(request.ConnectionString, Upsert(4, 1, "2"));

        // assert
        AssertState(request.ConnectionString, ["7/1: 2"]);
    }

    /// <summary>
    /// The previous entries are deleted before the new ones are written, so exactly one survives per script.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep one journal entry per account states script across a change of both settings")]
    public void Migration_Should_keep_one_journal_entry_per_account_states_script_across_a_change_of_both_settings()
    {
        // arrange
        const string refreshEntry = @"^\[clickhouse/post\] account_states\.sql::[0-9a-f]{32}$";
        const string liveEntry = @"^\[clickhouse/views\] account_states_live\.sql::[0-9a-f]{32}$";
        const string retentionEntry = @"^\[clickhouse/post\] account_state_events_ttl\.sql::[0-9a-f]{32}$";

        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var before = AccountStatesJournalEntries(request.ConnectionString);

        // act
        AssertSuccessful(CreateRunner().Run(WithVariables(request, "account_states_refresh_seconds=5", "account_state_events_delete_ttl_days=7")));

        // assert, the create and live view scripts once and untouched, the post scripts once each under a new hash
        var after = AccountStatesJournalEntries(request.ConnectionString);

        Assert.Collection(before,
            retention => Assert.Matches(retentionEntry, retention),
            refresh => Assert.Matches(refreshEntry, refresh),
            live => Assert.Matches(liveEntry, live),
            table => Assert.Equal("[clickhouse] 20261104_01_create_account_state_events.sql", table));

        Assert.Collection(after,
            retention => Assert.Matches(retentionEntry, retention),
            refresh => Assert.Matches(refreshEntry, refresh),
            live => Assert.Equal(before[2], live),
            table => Assert.Equal(before[3], table));

        Assert.NotEqual(before[0], after[0]);
        Assert.NotEqual(before[1], after[1]);
    }

    /// <summary>
    /// A forced run re-applies the retention, the refresh period and the grants over live data; none may touch a row.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep the stored accounts and their state through a re-run and a forced re-run")]
    public void Migration_Should_keep_the_stored_accounts_and_their_state_through_a_re_run_and_a_forced_re_run()
    {
        // arrange, a refresh only on demand, so the state read afterwards is the one computed before the re-runs
        var request = CreateRequest($"account_states_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1, 2));
        Refresh(request.ConnectionString);

        var stored = Sequences(request.ConnectionString);

        // act
        AssertSuccessful(CreateRunner().Run(request));
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Equal(stored, Sequences(request.ConnectionString));
        Assert.Equal(["7/1: 1", "7/2: 1"], State(request.ConnectionString));

        // and the exporter still writes after its role was revoked and granted again
        Insert(ExporterConnectionString(request), Upsert(5, 3));

        Assert.Equal([1L, 2L, 3L, 4L, 5L], Sequences(request.ConnectionString));
    }

    [Fact(DisplayName = "Migration Should create, refresh and grant a renamed account_state_events table and view")]
    public void Migration_Should_create_refresh_and_grant_a_renamed_account_state_events_table_and_view()
    {
        // arrange
        var request = CreateRequest("account_state_events_table=account_changes", "account_states_view=current_account_states", "account_states_live_view=current_account_states_live");

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert
        Assert.Equal(["account_changes", "current_account_states", "current_account_states_live", "position_events", "positions", "positions_live", "schema_versions", "ticks"], Tables(request.ConnectionString));
        Assert.Equal(AgreedTable("account_changes"), ShowCreate(request.ConnectionString, "account_changes"));
        Assert.Contains("FROM current_account_states_live", ShowCreate(request.ConnectionString, "current_account_states"));
        Assert.Contains("FROM account_changes AS account_state_events\nFINAL", ShowCreate(request.ConnectionString, "current_account_states_live"));

        Execute(ExporterConnectionString(request), InsertInto("account_changes", Snapshot(1, Sender, 1)));
        Refresh(request.ConnectionString, "current_account_states");

        Assert.Equal(3, Scalar<long>(ReaderConnectionString(request), "SELECT count() FROM account_changes"));
        Assert.Equal(["7/1: 1"], State(ReaderConnectionString(request), "current_account_states"));
        Assert.Equal(["7/1: 1"], State(ReaderConnectionString(request), "current_account_states_live"));
    }

    /// <summary>
    /// MODIFY REFRESH changes the schedule only: the rows of the view stay until the next refresh replaces them.
    /// </summary>
    [Fact(DisplayName = "Refresh Should switch the view to a new refresh period and keep its rows when the migration re-runs")]
    public void Refresh_Should_switch_the_view_to_a_new_refresh_period_and_keep_its_rows_when_the_migration_re_runs()
    {
        // arrange
        var request = CreateRequest($"account_states_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1));
        Refresh(request.ConnectionString);

        // act
        var changed = CreateRunner().Run(WithVariables(request, "account_states_refresh_seconds=5"));

        // assert
        AssertSuccessful(changed);

        Assert.Equal(1, changed.Sum(step => step.Result.Scripts.Count()));
        Assert.Contains("REFRESH EVERY 5 SECOND", ShowCreate(request.ConnectionString, "account_states"));
        Assert.Equal(["7/1: 1"], State(request.ConnectionString));
    }

    [Fact(DisplayName = "Retention Should replace the Delete retention when the migration re-runs with a new one")]
    public void Retention_Should_replace_the_Delete_retention_when_the_migration_re_runs_with_a_new_one()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        // act
        AssertSuccessful(CreateRunner().Run(WithVariables(request, "account_state_events_delete_ttl_days=7")));

        // assert
        Assert.Equal(AgreedTable(deleteTtlDays: 7), ShowCreate(request.ConnectionString, "account_state_events"));
    }

    /// <summary>
    /// CREATE TABLE runs once, so a new merge age reaches an existing table through post/account_state_events_ttl.sql.
    /// </summary>
    [Fact(DisplayName = "Retention Should replace the forced merge age when the migration re-runs with a new one")]
    public void Retention_Should_replace_the_forced_merge_age_when_the_migration_re_runs_with_a_new_one()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        // act
        AssertSuccessful(CreateRunner().Run(WithVariables(request, "account_state_events_min_age_to_force_merge_seconds=120")));

        // assert
        Assert.Equal(AgreedTable(forceMergeSeconds: 120), ShowCreate(request.ConnectionString, "account_state_events"));
    }

    [Fact(DisplayName = "Retention Should apply the forced merge age from the variables on a new database")]
    public void Retention_Should_apply_the_forced_merge_age_from_the_variables_on_a_new_database()
    {
        // act
        var connectionString = Migrate("account_state_events_min_age_to_force_merge_seconds=120");

        // assert
        Assert.Equal(AgreedTable(forceMergeSeconds: 120), ShowCreate(connectionString, "account_state_events"));
    }

    /// <summary>
    /// Only Delete rows expire: an old Upsert is an account that still exists, and the markers carry the watermark.
    /// </summary>
    [Fact(DisplayName = "Retention Should remove a Delete row older than the retention and keep every other row")]
    public void Retention_Should_remove_a_Delete_row_older_than_the_retention_and_keep_every_other_row()
    {
        // arrange, the default 30 days
        var connectionString = Migrate();

        Insert(connectionString,
            SnapshotStart(1) with { BatchTime = DaysAgo(60) },
            Upsert(2, 1) with { BatchTime = DaysAgo(60) },
            SnapshotEnd(3) with { BatchTime = DaysAgo(60) },
            Delete(4, 2) with { BatchTime = DaysAgo(60) },
            Delete(5, 3) with { BatchTime = HoursAgo(1) });

        // act
        ApplyRetention(connectionString);

        // assert
        Assert.Equal([1L, 2L, 3L, 5L], Sequences(connectionString));
    }

    /// <summary>
    /// Once the merges have collapsed an account into its Delete row, expiring that row leaves nothing to come back.
    /// </summary>
    [Fact(DisplayName = "Retention Should keep a deleted account deleted once its Delete row has expired")]
    public void Retention_Should_keep_a_deleted_account_deleted_once_its_Delete_row_has_expired()
    {
        // arrange, an account snapshotted, then deleted, and the merge collapsing it into its Delete row
        var request = CreateRequest($"account_states_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1, 2));
        Insert(request.ConnectionString, Delete(5, 2));
        Execute(request.ConnectionString, "OPTIMIZE TABLE account_state_events FINAL");

        // act, the Delete row ages past the retention
        Execute(request.ConnectionString,
            "ALTER TABLE account_state_events UPDATE batch_time = batch_time - INTERVAL 60 DAY WHERE trading_account_id = 2 SETTINGS mutations_sync = 2");
        ApplyRetention(request.ConnectionString);
        Refresh(request.ConnectionString);

        // assert, the account is gone from the table, not only from the view
        Assert.Equal(0, Scalar<long>(request.ConnectionString, "SELECT count() FROM account_state_events WHERE trading_account_id = 2"));
        Assert.Equal(["7/1: 1"], State(request.ConnectionString));
    }

    /// <summary>
    /// What the Delete retention relies on: min_age_to_force_merge_seconds merges the parts of a partition by itself,
    /// with no OPTIMIZE and no further insert, so the Delete row has replaced its account long before it expires.
    /// The age is cut to a second here so the test waits for the merge scheduler rather than ten minutes.
    /// </summary>
    [Fact(DisplayName = "Retention Should see a Delete row merged with its account by the forced merges alone, before it expires")]
    public void Retention_Should_see_a_Delete_row_merged_with_its_account_by_the_forced_merges_alone_before_it_expires()
    {
        // arrange, the snapshot and the delete in two batches, so in two parts
        var request = CreateRequest($"account_states_refresh_seconds={NoScheduledRefresh}", "account_state_events_min_age_to_force_merge_seconds=1");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1, 2));
        Insert(request.ConnectionString, Delete(5, 2));

        // act, nothing but waiting
        var merged = WaitUntil(TimeSpan.FromMinutes(3),
            () => Scalar<long>(request.ConnectionString, "SELECT count() FROM system.parts WHERE database = currentDatabase() AND table = 'account_state_events' AND active") == 1);

        // assert, one part, and in it account 2 is its Delete row alone - read without FINAL
        Assert.True(merged, "the parts were not merged by themselves within three minutes");
        Assert.Equal(["5 Delete"], Strings(request.ConnectionString,
            "SELECT concat(toString(sequence), ' ', toString(event_type)) FROM account_state_events WHERE trading_account_id = 2"));

        // and once that row expires, the account stays deleted
        Execute(request.ConnectionString,
            "ALTER TABLE account_state_events UPDATE batch_time = batch_time - INTERVAL 60 DAY WHERE trading_account_id = 2 SETTINGS mutations_sync = 2");
        ApplyRetention(request.ConnectionString);

        AssertState(request.ConnectionString, ["7/1: 1"]);
    }

    private static bool WaitUntil(TimeSpan timeout, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(500));
        }

        return true;
    }

    /// <summary>
    /// The cost of a TTL on the Delete rows: one that is already expired when it arrives is dropped before any merge
    /// can collapse the account into it, so the account's last Upsert comes back.
    /// </summary>
    [Fact(DisplayName = "Retention Should bring a deleted account back when its Delete row arrives already expired")]
    public void Retention_Should_bring_a_deleted_account_back_when_its_Delete_row_arrives_already_expired()
    {
        // arrange
        var request = CreateRequest($"account_states_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1, 2));

        // act, a Delete stamped 60 days back, past the default 30
        Insert(request.ConnectionString, Delete(5, 2) with { BatchTime = DaysAgo(60) });
        Execute(request.ConnectionString, "OPTIMIZE TABLE account_state_events FINAL");
        Refresh(request.ConnectionString);

        // assert
        Assert.Equal(["7/1: 1", "7/2: 1"], State(request.ConnectionString));
    }

    /// <summary>
    /// The production path: the exporter's own account, which may insert and do nothing else.
    /// </summary>
    [Fact(DisplayName = "Grants Should let the exporter insert account states and do nothing else to them or to the view")]
    public void Grants_Should_let_the_exporter_insert_account_states_and_do_nothing_else_to_them_or_to_the_view()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var exporter = ExporterConnectionString(request);

        // act
        Insert(exporter, Snapshot(1, Sender, 1));

        // assert
        Assert.Equal(3, Count(request.ConnectionString, "account_state_events"));

        AssertDenied(exporter, "SELECT count() FROM account_state_events");
        AssertDenied(exporter, "SELECT count() FROM account_state_events FINAL");
        AssertDenied(exporter, "SELECT count() FROM account_states");
        AssertDenied(exporter, "SELECT count() FROM account_states_live");
        AssertDenied(exporter, "ALTER TABLE account_state_events DELETE WHERE 1 = 1");
        AssertDenied(exporter, "DELETE FROM account_state_events WHERE 1 = 1");
        AssertDenied(exporter, "TRUNCATE TABLE account_state_events");
        AssertDenied(exporter, "OPTIMIZE TABLE account_state_events FINAL");
        AssertDenied(exporter, "ALTER TABLE account_state_events MODIFY TTL toDateTime(batch_time) + INTERVAL 1 DAY");
        AssertDenied(exporter, "SYSTEM REFRESH VIEW account_states");
        AssertDenied(exporter, "ALTER TABLE account_states MODIFY REFRESH EVERY 1 SECOND");
        AssertDenied(exporter, "DROP TABLE account_states");
        AssertDenied(exporter, "DROP VIEW account_states_live");
        AssertDenied(exporter, "DROP TABLE account_state_events");

        Assert.Equal(3, Count(request.ConnectionString, "account_state_events"));
    }

    [Fact(DisplayName = "Grants Should let the reader read account states and do nothing else to them")]
    public void Grants_Should_let_the_reader_read_account_states_and_do_nothing_else_to_them()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        Insert(request.ConnectionString, Snapshot(1, Sender, 1));
        Refresh(request.ConnectionString);

        var reader = ReaderConnectionString(request);

        // act, assert
        Assert.Equal(3, Count(reader, "account_state_events"));
        Assert.Equal(3, Count(reader, "account_state_events FINAL"));
        Assert.Equal(["7/1: 1"], State(reader));
        Assert.Equal(["7/1: 1"], State(reader, "account_states_live"));

        AssertDenied(reader, InsertSql(Upsert(4, 2)));
        AssertDenied(reader, "ALTER TABLE account_state_events DELETE WHERE 1 = 1");
        AssertDenied(reader, "TRUNCATE TABLE account_state_events");
        AssertDenied(reader, "OPTIMIZE TABLE account_state_events FINAL");
        AssertDenied(reader, "SYSTEM REFRESH VIEW account_states");
        AssertDenied(reader, "DROP TABLE account_states");
        AssertDenied(reader, "DROP VIEW account_states_live");
        AssertDenied(reader, "DROP TABLE account_state_events");

        Assert.Equal(3, Count(request.ConnectionString, "account_state_events"));
    }

    /// <summary>
    /// Roles are named after the database, so deployments sharing a server cannot reach each other's account states.
    /// </summary>
    [Fact(DisplayName = "Grants Should confine the accounts of each deployment to its own account states")]
    public void Grants_Should_confine_the_accounts_of_each_deployment_to_its_own_account_states()
    {
        // arrange, two deployments on one server
        var first = CreateRequest();
        var second = CreateRequest();
        AssertSuccessful(CreateRunner().Run(first));
        AssertSuccessful(CreateRunner().Run(second));

        // act, assert
        Insert(ExporterConnectionString(first), Upsert(1, 1));

        AssertDenied(ExporterConnectionString(first), InsertInto($"{second.DatabaseName}.account_state_events", Upsert(1, 1)));
        AssertDenied(ReaderConnectionString(first), $"SELECT count() FROM {second.DatabaseName}.account_state_events");
        AssertDenied(ReaderConnectionString(first), $"SELECT count() FROM {second.DatabaseName}.account_states");
        AssertDenied(ReaderConnectionString(first), $"SELECT count() FROM {second.DatabaseName}.account_states_live");

        Assert.Equal(1, Count(first.ConnectionString, "account_state_events"));
        Assert.Equal(0, Count(second.ConnectionString, "account_state_events"));
    }
}
