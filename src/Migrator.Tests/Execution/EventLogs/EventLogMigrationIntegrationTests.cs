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

using Npgsql;
using Xunit.Abstractions;

namespace YB.TradeServer.Db.Migrator.Tests.Execution.EventLogs;

/// <summary>
/// What later migrations do to a live log and its derived table: re-runs, forced re-runs, a dropped trigger, a purged
/// log, and tables the deployment named itself.
/// </summary>
[Trait("Category", "Integration")]
public class EventLogMigrationIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    [Theory(DisplayName = "Migration Should create and project the log under the names a deployment chose")]
    [EveryEventLog]
    public void Migration_Should_create_and_project_the_log_under_the_names_a_deployment_chose(EventLog log)
    {
        // arrange, act
        Migrate(log, renamed: true);

        Export(State(1), State(2));
        Export(ResyncStarted());
        Export(State(1, version: 2));
        Export(ResyncFinished());

        // assert, the projection, its trigger, its indexes and the view all answer to the chosen names
        Assert.Equal(["7/1 v2"], Rows());
        Assert.Equal([$"CREATE TRIGGER {Log.Trigger} AFTER INSERT ON public.{Log.Events} FOR EACH ROW EXECUTE FUNCTION {Log.Function}()"],
            TriggerDefinitions(Log.Events));
        Assert.Equal(new[] { Log.HistoryIndex, Log.MarkerIndex, $"{Log.Events}_pkey" }.Order(StringComparer.Ordinal), IndexNames(Log.Events));
        Assert.False(Scalar<bool>(Reader, $"SELECT resync_in_progress FROM {Log.ResyncView}"));

        // and nothing answers to the default ones
        Assert.Empty(ObjectsUnderTheDefaultNames(log));
    }

    /// <summary>
    /// A create script never runs twice, so the projection script trips over the default names and rolls back alone.
    /// </summary>
    [Theory(DisplayName = "Migration Should fail rather than fork a second log when the chosen names are not passed again")]
    [EveryEventLog]
    public void Migration_Should_fail_rather_than_fork_a_second_log_when_the_chosen_names_are_not_passed_again(EventLog log)
    {
        // arrange, a deployment that exports under names of its own
        Migrate(log, renamed: true);
        Export(State(1));

        // act, a run that forgets them
        var forgotten = Run(Request with { Variables = Variables(Request.DatabaseName) });

        // assert
        var (phase, failed) = forgotten[^1];

        Assert.Equal("views", phase);
        Assert.False(failed.Successful);
        Assert.StartsWith($"[psql/views] {Log.ProjectionScript}::", failed.ErrorScript?.Name);
        Assert.Equal(PostgresErrorCodes.UndefinedTable, Assert.IsType<PostgresException>(failed.Error).SqlState);

        Assert.Empty(ObjectsUnderTheDefaultNames(log));

        // and the deployment's own projection carries on, so the corrected run has nothing to repair
        Export(State(2));

        Assert.Equal(["7/1 v1", "7/2 v1"], Rows());
        AssertSuccessful(Run(Request));
    }

    [Theory(DisplayName = "Migration Should keep the derived table through a re-run and a forced re-run in the middle of a resync")]
    [EveryEventLog]
    public void Migration_Should_keep_the_derived_table_through_a_re_run_and_a_forced_re_run_in_the_middle_of_a_resync(EventLog log)
    {
        // arrange, half a resync, so the reconcile has every kind of row to get wrong
        Migrate(log);
        Export(State(1, label: "re-announced"), State(2, label: "not reached yet"), State(3, label: "deleted"), Deleted(3));
        Export(ResyncStarted());
        Export(State(1, version: 2, label: "re-announced"));

        var before = RowsWithStamps();

        // act
        MigrateAgain();
        MigrateAgain(force: true);

        // assert, nothing swept early and nothing deleted brought back
        Assert.Equal(before, RowsWithStamps());
        Assert.Equal(["7/1 v2 re-announced", "7/2 v1 not reached yet"], Rows());
        Assert.True(ResyncStatuses().Single().InProgress);

        // and the resync finishes as it would have without the migrations
        Export(ResyncFinished());

        Assert.Equal(["7/1 v2 re-announced"], Rows());
    }

    /// <summary>
    /// A plain run re-applies only scripts whose content changed, and a dropped trigger changes none of them.
    /// </summary>
    [Theory(DisplayName = "Migration Should leave a dropped trigger dropped until a forced re-run restores it")]
    [EveryEventLog]
    public void Migration_Should_leave_a_dropped_trigger_dropped_until_a_forced_re_run_restores_it(EventLog log)
    {
        // arrange, the trigger is dropped by hand and the export carries on
        Migrate(log);
        DropTrigger();
        Export(State(1));

        // act, assert, a plain re-run repairs nothing
        MigrateAgain();

        Assert.Empty(TriggerDefinitions(Log.Events));
        Assert.Empty(Rows());

        // a forced one restores the trigger and catches the table up
        MigrateAgain(force: true);

        Assert.Single(TriggerDefinitions(Log.Events));
        Assert.Equal(["7/1 v1"], Rows());

        Export(State(2));

        Assert.Equal(["7/1 v1", "7/2 v1"], Rows());
    }

    /// <summary>
    /// A sweep leaves no event behind, so a reconcile replaying only the newest event once brought swept entities back.
    /// </summary>
    [Theory(DisplayName = "Regression Should not bring back a swept entity when the projection runs again")]
    [EveryEventLog]
    public void Regression_Should_not_bring_back_a_swept_entity_when_the_projection_runs_again(EventLog log)
    {
        // arrange, 2 is swept by a finished resync, then the trigger is dropped and the export carries on
        Migrate(log);
        Export(State(1), State(2));
        Export(ResyncStarted());
        Export(State(1));
        Export(ResyncFinished());

        DropTrigger();
        Export(State(3, label: "while dropped"));
        Export(Deleted(1));

        // act
        MigrateAgain(force: true);

        // assert, the table catches up and the swept entity stays swept
        Assert.Equal(["7/3 v1 while dropped"], Rows());
    }

    /// <summary>
    /// The sweep lives in the trigger, so a resync that ended while it was missing has to be swept by the reconcile.
    /// </summary>
    [Theory(DisplayName = "Regression Should apply a resync sweep that happened while the trigger was missing")]
    [EveryEventLog]
    public void Regression_Should_apply_a_resync_sweep_that_happened_while_the_trigger_was_missing(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1, label: "re-announced"), State(2, label: "left out"));

        // act, the trigger is missing for the whole resync, which re-announces 1 only
        DropTrigger();
        Export(ResyncStarted());
        Export(State(1, label: "re-announced"));
        Export(ResyncFinished());

        MigrateAgain(force: true);

        // assert
        Assert.Equal(["7/1 v1 re-announced"], Rows());
    }

    /// <summary>
    /// The UPDATE stands in for a row an earlier, buggy projection wrote: the right stamp over the wrong content.
    /// </summary>
    [Theory(DisplayName = "Regression Should correct a row whose content disagrees with its event")]
    [EveryEventLog]
    public void Regression_Should_correct_a_row_whose_content_disagrees_with_its_event(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1, label: "correct"));
        Execute($"UPDATE {Log.State} SET {Log.LabelColumn} = 'corrupted'");

        // act
        MigrateAgain(force: true);

        // assert, the log wins
        Assert.Equal(["7/1 v1 correct"], Rows());
    }

    /// <summary>
    /// With no event left the reconcile has nothing to replay, and must not read that as a reason to delete.
    /// </summary>
    [Theory(DisplayName = "Retention Should keep the rows whose events have aged out through a forced re-run")]
    [EveryEventLog]
    public void Retention_Should_keep_the_rows_whose_events_have_aged_out_through_a_forced_re_run(EventLog log)
    {
        // arrange, a finished resync, then a purge of the whole log
        Migrate(log);
        Export(State(1), State(2));
        Export(ResyncStarted());
        Export(State(1, version: 2));
        Export(ResyncFinished());

        Execute($"DELETE FROM {Log.Events}");

        // act
        MigrateAgain(force: true);

        // assert
        Assert.Equal(["7/1 v2"], Rows());
    }

    /// <summary>
    /// The relations and the function a run would create for the log under its default names.
    /// </summary>
    private List<string> ObjectsUnderTheDefaultNames(EventLog log)
    {
        var defaults = EventLogTables.Of(log);

        return Strings($"""
                        SELECT relname::text FROM pg_class WHERE relname IN ('{defaults.Events}', '{defaults.State}', '{defaults.ResyncView}')
                        UNION ALL
                        SELECT proname::text FROM pg_proc WHERE proname = '{defaults.Function}'
                        """);
    }

    private List<string> IndexNames(string table)
    {
        return [.. Strings($"SELECT indexname::text FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{table}'").Order(StringComparer.Ordinal)];
    }
}
