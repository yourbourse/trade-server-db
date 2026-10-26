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
/// What the migration makes of the positions table and the positions_state and positions_state_live views: their
/// definitions, re-runs, the refresh period, retention and grants.
/// </summary>
[Trait("Category", "Integration")]
public class PositionsMigrationIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : PositionsTestBase(configBuilder, output)
{
    /// <summary>
    /// SHOW CREATE TABLE in full: fails on any change to a column, type, key, TTL or setting.
    /// </summary>
    [Fact(DisplayName = "Schema Should create the positions table with the agreed columns, engine, keys and retention")]
    public void Schema_Should_create_the_positions_table_with_the_agreed_columns_engine_keys_and_retention()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.Equal(AgreedTable(), ShowCreate(connectionString, "positions"));
    }

    [Fact(DisplayName = "Schema Should create the positions_state view refreshed every 30 seconds into a MergeTree")]
    public void Schema_Should_create_the_positions_state_view_refreshed_every_30_seconds_into_a_MergeTree()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        var view = ShowCreate(connectionString, "positions_state");

        Assert.StartsWith("CREATE MATERIALIZED VIEW positions_state", view);
        Assert.Contains("REFRESH EVERY 30 SECOND", view);
        Assert.Contains("ENGINE = MergeTree", view);
        Assert.Contains("ORDER BY (sender_id, position_id)", view);
        Assert.Contains("FROM positions_state_live", view);
    }

    /// <summary>
    /// The one definition of the state: a plain view reading positions with FINAL, as the reader who queries it.
    /// </summary>
    [Fact(DisplayName = "Schema Should create the positions_state_live view over positions with FINAL")]
    public void Schema_Should_create_the_positions_state_live_view_over_positions_with_FINAL()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        var view = ShowCreate(connectionString, "positions_state_live");

        Assert.StartsWith("CREATE VIEW positions_state_live", view);
        Assert.DoesNotContain("DEFINER", view); // the default, INVOKER, is not printed
        Assert.Contains("FROM positions AS positions\nFINAL", view);
        Assert.Equal("View", Scalar<string>(connectionString,
            "SELECT engine FROM system.tables WHERE database = currentDatabase() AND name = 'positions_state_live'"));
    }

    /// <summary>
    /// Both views expose every column the exporter writes, in the table's order, and not marker_sequence.
    /// </summary>
    [Theory(DisplayName = "Schema Should give a positions view every exported column and no marker_sequence")]
    [InlineData("positions_state")]
    [InlineData("positions_state_live")]
    public void Schema_Should_give_a_positions_view_every_exported_column_and_no_marker_sequence(string view)
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.Equal(PositionColumns.Split(", "), Strings(connectionString,
            $"SELECT name FROM system.columns WHERE database = currentDatabase() AND table = '{view}' ORDER BY position"));
    }

    /// <summary>
    /// views/ re-runs a changed script; CREATE OR REPLACE swaps the live view's query, and positions_state follows it.
    /// </summary>
    [Fact(DisplayName = "Migration Should replace the live view and keep positions_state reading it when the live script re-runs")]
    public void Migration_Should_replace_the_live_view_and_keep_positions_state_reading_it_when_the_live_script_re_runs()
    {
        // arrange
        var request = CreateRequest($"positions_state_refresh_seconds={NoScheduledRefresh}");
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
    [Fact(DisplayName = "Migration Should keep one journal entry per positions script across a change of both settings")]
    public void Migration_Should_keep_one_journal_entry_per_positions_script_across_a_change_of_both_settings()
    {
        // arrange
        const string refreshEntry = @"^\[clickhouse/post\] positions_state\.sql::[0-9a-f]{32}$";
        const string liveEntry = @"^\[clickhouse/views\] positions_state_live\.sql::[0-9a-f]{32}$";
        const string retentionEntry = @"^\[clickhouse/post\] positions_ttl\.sql::[0-9a-f]{32}$";

        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var before = PositionsJournalEntries(request.ConnectionString);

        // act
        AssertSuccessful(CreateRunner().Run(WithVariables(request, "positions_state_refresh_seconds=5", "positions_delete_ttl_days=7")));

        // assert, the create and live view scripts once and untouched, the post scripts once each under a new hash
        var after = PositionsJournalEntries(request.ConnectionString);

        Assert.Collection(before,
            refresh => Assert.Matches(refreshEntry, refresh),
            retention => Assert.Matches(retentionEntry, retention),
            live => Assert.Matches(liveEntry, live),
            table => Assert.Equal("[clickhouse] 20261023_01_create_positions.sql", table));

        Assert.Collection(after,
            refresh => Assert.Matches(refreshEntry, refresh),
            retention => Assert.Matches(retentionEntry, retention),
            live => Assert.Equal(before[2], live),
            table => Assert.Equal(before[3], table));

        Assert.NotEqual(before[0], after[0]);
        Assert.NotEqual(before[1], after[1]);
    }

    /// <summary>
    /// A forced run re-applies the retention, the refresh period and the grants over live data; none may touch a row.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep the stored positions and their state through a re-run and a forced re-run")]
    public void Migration_Should_keep_the_stored_positions_and_their_state_through_a_re_run_and_a_forced_re_run()
    {
        // arrange, a refresh only on demand, so the state read afterwards is the one computed before the re-runs
        var request = CreateRequest($"positions_state_refresh_seconds={NoScheduledRefresh}");
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

    [Fact(DisplayName = "Migration Should create, refresh and grant a renamed positions table and view")]
    public void Migration_Should_create_refresh_and_grant_a_renamed_positions_table_and_view()
    {
        // arrange
        var request = CreateRequest("positions_table=position_changes", "positions_state_view=open_positions", "positions_state_live_view=open_positions_live");

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert
        Assert.Equal(["open_positions", "open_positions_live", "position_changes", "schema_versions", "ticks"], Tables(request.ConnectionString));
        Assert.Equal(AgreedTable("position_changes"), ShowCreate(request.ConnectionString, "position_changes"));
        Assert.Contains("FROM open_positions_live", ShowCreate(request.ConnectionString, "open_positions"));
        Assert.Contains("FROM position_changes AS positions\nFINAL", ShowCreate(request.ConnectionString, "open_positions_live"));

        Execute(ExporterConnectionString(request), InsertInto("position_changes", Snapshot(1, Sender, 1)));
        Refresh(request.ConnectionString, "open_positions");

        Assert.Equal(3, Scalar<long>(ReaderConnectionString(request), "SELECT count() FROM position_changes"));
        Assert.Equal(["7/1: 1"], State(ReaderConnectionString(request), "open_positions"));
        Assert.Equal(["7/1: 1"], State(ReaderConnectionString(request), "open_positions_live"));
    }

    /// <summary>
    /// MODIFY REFRESH changes the schedule only: the rows of the view stay until the next refresh replaces them.
    /// </summary>
    [Fact(DisplayName = "Refresh Should switch the view to a new refresh period and keep its rows when the migration re-runs")]
    public void Refresh_Should_switch_the_view_to_a_new_refresh_period_and_keep_its_rows_when_the_migration_re_runs()
    {
        // arrange
        var request = CreateRequest($"positions_state_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1));
        Refresh(request.ConnectionString);

        // act
        var changed = CreateRunner().Run(WithVariables(request, "positions_state_refresh_seconds=5"));

        // assert
        AssertSuccessful(changed);

        Assert.Equal(1, changed.Sum(step => step.Result.Scripts.Count()));
        Assert.Contains("REFRESH EVERY 5 SECOND", ShowCreate(request.ConnectionString, "positions_state"));
        Assert.Equal(["7/1: 1"], State(request.ConnectionString));
    }

    [Fact(DisplayName = "Retention Should replace the Delete retention when the migration re-runs with a new one")]
    public void Retention_Should_replace_the_Delete_retention_when_the_migration_re_runs_with_a_new_one()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        // act
        AssertSuccessful(CreateRunner().Run(WithVariables(request, "positions_delete_ttl_days=7")));

        // assert
        Assert.Equal(AgreedTable(deleteTtlDays: 7), ShowCreate(request.ConnectionString, "positions"));
    }

    /// <summary>
    /// Only Delete rows expire: an old Upsert is a position still open, and the markers carry the watermark.
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
    /// Once the merges have collapsed a position into its Delete row, expiring that row leaves nothing to come back.
    /// </summary>
    [Fact(DisplayName = "Retention Should keep a closed position closed once its Delete row has expired")]
    public void Retention_Should_keep_a_closed_position_closed_once_its_Delete_row_has_expired()
    {
        // arrange, a position snapshotted, then closed, and the merge collapsing it into its Delete row
        var request = CreateRequest($"positions_state_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1, 2));
        Insert(request.ConnectionString, Delete(5, 2));
        Execute(request.ConnectionString, "OPTIMIZE TABLE positions FINAL");

        // act, the Delete row ages past the retention
        Execute(request.ConnectionString,
            "ALTER TABLE positions UPDATE batch_time = batch_time - INTERVAL 60 DAY WHERE position_id = 2 SETTINGS mutations_sync = 2");
        ApplyRetention(request.ConnectionString);
        Refresh(request.ConnectionString);

        // assert, the position is gone from the table, not only from the view
        Assert.Equal(0, Scalar<long>(request.ConnectionString, "SELECT count() FROM positions WHERE position_id = 2"));
        Assert.Equal(["7/1: 1"], State(request.ConnectionString));
    }

    /// <summary>
    /// What the Delete retention relies on: min_age_to_force_merge_seconds merges the parts of a partition by itself,
    /// with no OPTIMIZE and no further insert, so the Delete row has replaced its position long before it expires.
    /// The age is cut to a second here so the test waits for the merge scheduler rather than ten minutes.
    /// </summary>
    [Fact(DisplayName = "Retention Should see a Delete row merged with its position by the forced merges alone, before it expires")]
    public void Retention_Should_see_a_Delete_row_merged_with_its_position_by_the_forced_merges_alone_before_it_expires()
    {
        // arrange, the snapshot and the close in two batches, so in two parts
        var request = CreateRequest($"positions_state_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));
        Execute(request.ConnectionString, "ALTER TABLE positions MODIFY SETTING min_age_to_force_merge_seconds = 1");

        Insert(request.ConnectionString, Snapshot(1, Sender, 1, 2));
        Insert(request.ConnectionString, Delete(5, 2));

        // act, nothing but waiting
        var merged = WaitUntil(TimeSpan.FromMinutes(3),
            () => Scalar<long>(request.ConnectionString, "SELECT count() FROM system.parts WHERE database = currentDatabase() AND table = 'positions' AND active") == 1);

        // assert, one part, and in it position 2 is its Delete row alone - read without FINAL
        Assert.True(merged, "the parts were not merged by themselves within three minutes");
        Assert.Equal(["5 Delete"], Strings(request.ConnectionString,
            "SELECT concat(toString(sequence), ' ', toString(event_type)) FROM positions WHERE position_id = 2"));

        // and once that row expires, the position stays closed
        Execute(request.ConnectionString,
            "ALTER TABLE positions UPDATE batch_time = batch_time - INTERVAL 60 DAY WHERE position_id = 2 SETTINGS mutations_sync = 2");
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
    /// can collapse the position into it, so the position's last Upsert comes back.
    /// </summary>
    [Fact(DisplayName = "Retention Should bring a closed position back when its Delete row arrives already expired")]
    public void Retention_Should_bring_a_closed_position_back_when_its_Delete_row_arrives_already_expired()
    {
        // arrange
        var request = CreateRequest($"positions_state_refresh_seconds={NoScheduledRefresh}");
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, Snapshot(1, Sender, 1, 2));

        // act, a Delete stamped 60 days back, past the default 30
        Insert(request.ConnectionString, Delete(5, 2) with { BatchTime = DaysAgo(60) });
        Execute(request.ConnectionString, "OPTIMIZE TABLE positions FINAL");
        Refresh(request.ConnectionString);

        // assert
        Assert.Equal(["7/1: 1", "7/2: 1"], State(request.ConnectionString));
    }

    /// <summary>
    /// The production path: the exporter's own account, which may insert and do nothing else.
    /// </summary>
    [Fact(DisplayName = "Grants Should let the exporter insert positions and do nothing else to them or to the view")]
    public void Grants_Should_let_the_exporter_insert_positions_and_do_nothing_else_to_them_or_to_the_view()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var exporter = ExporterConnectionString(request);

        // act
        Insert(exporter, Snapshot(1, Sender, 1));

        // assert
        Assert.Equal(3, Count(request.ConnectionString, "positions"));

        AssertDenied(exporter, "SELECT count() FROM positions");
        AssertDenied(exporter, "SELECT count() FROM positions FINAL");
        AssertDenied(exporter, "SELECT count() FROM positions_state");
        AssertDenied(exporter, "SELECT count() FROM positions_state_live");
        AssertDenied(exporter, "ALTER TABLE positions DELETE WHERE 1 = 1");
        AssertDenied(exporter, "DELETE FROM positions WHERE 1 = 1");
        AssertDenied(exporter, "TRUNCATE TABLE positions");
        AssertDenied(exporter, "OPTIMIZE TABLE positions FINAL");
        AssertDenied(exporter, "ALTER TABLE positions MODIFY TTL toDateTime(batch_time) + INTERVAL 1 DAY");
        AssertDenied(exporter, "SYSTEM REFRESH VIEW positions_state");
        AssertDenied(exporter, "ALTER TABLE positions_state MODIFY REFRESH EVERY 1 SECOND");
        AssertDenied(exporter, "DROP TABLE positions_state");
        AssertDenied(exporter, "DROP VIEW positions_state_live");
        AssertDenied(exporter, "DROP TABLE positions");

        Assert.Equal(3, Count(request.ConnectionString, "positions"));
    }

    [Fact(DisplayName = "Grants Should let the reader read positions and their state and do nothing else to them")]
    public void Grants_Should_let_the_reader_read_positions_and_their_state_and_do_nothing_else_to_them()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        Insert(request.ConnectionString, Snapshot(1, Sender, 1));
        Refresh(request.ConnectionString);

        var reader = ReaderConnectionString(request);

        // act, assert
        Assert.Equal(3, Count(reader, "positions"));
        Assert.Equal(3, Count(reader, "positions FINAL"));
        Assert.Equal(["7/1: 1"], State(reader));
        Assert.Equal(["7/1: 1"], State(reader, "positions_state_live"));

        AssertDenied(reader, InsertSql(Upsert(4, 2)));
        AssertDenied(reader, "ALTER TABLE positions DELETE WHERE 1 = 1");
        AssertDenied(reader, "TRUNCATE TABLE positions");
        AssertDenied(reader, "OPTIMIZE TABLE positions FINAL");
        AssertDenied(reader, "SYSTEM REFRESH VIEW positions_state");
        AssertDenied(reader, "DROP TABLE positions_state");
        AssertDenied(reader, "DROP VIEW positions_state_live");
        AssertDenied(reader, "DROP TABLE positions");

        Assert.Equal(3, Count(request.ConnectionString, "positions"));
    }

    /// <summary>
    /// The settings that keep work off the insert path, set on the exporter's account by users/exporter.sql.
    /// </summary>
    [Fact(DisplayName = "Grants Should turn optimize_on_insert and materialize_statistics_on_insert off for the exporter")]
    public void Grants_Should_turn_optimize_on_insert_and_materialize_statistics_on_insert_off_for_the_exporter()
    {
        // arrange
        var request = CreateRequest();

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert
        Assert.Equal("0 0", Scalar<string>(ExporterConnectionString(request),
            "SELECT concat(toString(toUInt8(getSetting('optimize_on_insert'))), ' ', toString(toUInt8(getSetting('materialize_statistics_on_insert'))))"));
    }

    /// <summary>
    /// Roles are named after the database, so deployments sharing a server cannot reach each other's positions.
    /// </summary>
    [Fact(DisplayName = "Grants Should confine the accounts of each deployment to its own positions")]
    public void Grants_Should_confine_the_accounts_of_each_deployment_to_its_own_positions()
    {
        // arrange, two deployments on one server
        var first = CreateRequest();
        var second = CreateRequest();
        AssertSuccessful(CreateRunner().Run(first));
        AssertSuccessful(CreateRunner().Run(second));

        // act, assert
        Insert(ExporterConnectionString(first), Upsert(1, 1));

        AssertDenied(ExporterConnectionString(first), InsertInto($"{second.DatabaseName}.positions", Upsert(1, 1)));
        AssertDenied(ReaderConnectionString(first), $"SELECT count() FROM {second.DatabaseName}.positions");
        AssertDenied(ReaderConnectionString(first), $"SELECT count() FROM {second.DatabaseName}.positions_state");
        AssertDenied(ReaderConnectionString(first), $"SELECT count() FROM {second.DatabaseName}.positions_state_live");

        Assert.Equal(1, Count(first.ConnectionString, "positions"));
        Assert.Equal(0, Count(second.ConnectionString, "positions"));
    }
}
