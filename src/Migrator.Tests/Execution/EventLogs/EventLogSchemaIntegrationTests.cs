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
/// How each event log, the table derived from it and its resync view are declared, and the reads the indexes serve.
/// </summary>
[Trait("Category", "Integration")]
public class EventLogSchemaIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    [Theory(DisplayName = "Schema Should project through one trigger, after each inserted event and on nothing else")]
    [EveryEventLog]
    public void Schema_Should_project_through_one_trigger_after_each_inserted_event_and_on_nothing_else(EventLog log)
    {
        // arrange, act
        Migrate(log);

        // assert, so an UPDATE, DELETE or TRUNCATE of the log never reaches the derived table
        Assert.Equal([$"CREATE TRIGGER {Log.Trigger} AFTER INSERT ON public.{Log.Events} FOR EACH ROW EXECUTE FUNCTION {Log.Function}()"],
            TriggerDefinitions(Log.Events));

        Assert.Empty(TriggerDefinitions(Log.State));
    }

    [Theory(DisplayName = "Schema Should index the log for the history of an entity and for the resync markers")]
    [EveryEventLog]
    public void Schema_Should_index_the_log_for_the_history_of_an_entity_and_for_the_resync_markers(EventLog log)
    {
        // arrange, act
        Migrate(log);

        // assert
        string[] expected =
        [
            $"CREATE INDEX {Log.HistoryIndex} ON public.{Log.Events} USING btree (sender_id, {Log.IdColumn}, id)",
            $"CREATE INDEX {Log.MarkerIndex} ON public.{Log.Events} USING btree (sender_id, event_type, id) WHERE (event_type = ANY (ARRAY[2, 3]))",
            $"CREATE UNIQUE INDEX {Log.Events}_pkey ON public.{Log.Events} USING btree (id)"
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), IndexDefinitions(Log.Events));
    }

    /// <summary>
    /// A foreign key on source_event_row_id would make every retention purge fail, or cascade into the table.
    /// </summary>
    [Theory(DisplayName = "Schema Should key the derived table by sender and id, with no other index and no foreign key")]
    [EveryEventLog]
    public void Schema_Should_key_the_derived_table_by_sender_and_id_with_no_other_index_and_no_foreign_key(EventLog log)
    {
        // arrange, act
        Migrate(log);

        // assert
        Assert.Equal([$"CREATE UNIQUE INDEX {Log.State}_pkey ON public.{Log.State} USING btree (sender_id, {Log.IdColumn})"],
            IndexDefinitions(Log.State));

        Assert.Empty(Strings($"SELECT conname FROM pg_constraint WHERE contype = 'f' AND conrelid IN ('{Log.Events}'::regclass, '{Log.State}'::regclass)"));
    }

    /// <summary>
    /// A type or nullability that drifted between the two would not fail the migration; it would truncate, round or stall.
    /// </summary>
    [Theory(DisplayName = "Schema Should carry every column of the log in the derived table, with the same type and nullability")]
    [EveryEventLog]
    public void Schema_Should_carry_every_column_of_the_log_in_the_derived_table_with_the_same_type_and_nullability(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, every column but the envelope each table adds of its own
        var logColumns = ColumnDefinitions(Log.Events).Where(column => !column.StartsWith("id ") && !column.StartsWith("time_inserted ") && !column.StartsWith("event_type "));
        var derivedColumns = ColumnDefinitions(Log.State).Where(column => !column.StartsWith("source_event_row_id ") && !column.StartsWith("time_applied "));

        // assert
        Assert.Equal(logColumns.Order(StringComparer.Ordinal), derivedColumns.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The exporter writes \N for a group without an email service, so that column alone has to take NULL.
    /// </summary>
    [Theory(DisplayName = "Schema Should refuse NULL in every column, bar the email service of a group")]
    [EveryEventLog]
    public void Schema_Should_refuse_NULL_in_every_column_bar_the_email_service_of_a_group(EventLog log)
    {
        // arrange, act
        Migrate(log);

        // assert
        string[] nullable = log == EventLog.Groups ? ["email_service_id bigint null"] : [];

        Assert.Equal(nullable, ColumnDefinitions(Log.Events).Where(column => column.EndsWith(" null")));
        Assert.Equal(nullable, ColumnDefinitions(Log.State).Where(column => column.EndsWith(" null")));
    }

    /// <summary>
    /// The projection writes every column, so a default there could only hide one it forgot.
    /// </summary>
    [Theory(DisplayName = "Schema Should default nothing on the derived table but the time a row was applied")]
    [EveryEventLog]
    public void Schema_Should_default_nothing_on_the_derived_table_but_the_time_a_row_was_applied(EventLog log)
    {
        // arrange, act
        Migrate(log);

        // assert
        Assert.Equal(["time_applied = now()"], Defaults(Log.State));
    }

    [Theory(DisplayName = "Schema Should expose the five columns of the resync view")]
    [EveryEventLog]
    public void Schema_Should_expose_the_five_columns_of_the_resync_view(EventLog log)
    {
        // arrange, act
        Migrate(log);

        // assert
        Assert.Equal(
        [
            "sender_id bigint",
            "resync_started_event_row_id bigint",
            "time_resync_started timestamp with time zone",
            "time_resync_finished timestamp with time zone",
            "resync_in_progress boolean"
        ], ColumnDefinitions(Log.ResyncView));
    }

    /// <summary>
    /// The ro role reads every table, so a password, like the API's phonePassword, is kept out by having no column for it.
    /// </summary>
    [Theory(DisplayName = "Schema Should have no column that could hold credential material")]
    [EveryEventLog]
    public void Schema_Should_have_no_column_that_could_hold_credential_material(EventLog log)
    {
        // arrange, act
        Migrate(log);

        // assert, name and type both have to fit: time_password_last_changed is a timestamp
        Assert.Empty(Strings($"""
                              SELECT table_name || '.' || column_name
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name IN ('{Log.Events}', '{Log.State}')
                                AND (column_name ILIKE '%password%' OR column_name ILIKE '%secret%' OR column_name ILIKE '%credential%')
                                AND data_type IN ('character varying', 'character', 'text', 'bytea')
                              """));
    }

    [Theory(DisplayName = "Reading Should serve the history of an entity from the history index")]
    [EveryEventLog]
    public void Reading_Should_serve_the_history_of_an_entity_from_the_history_index(EventLog log)
    {
        // arrange, a log long enough for the planner to have a choice
        Migrate(log);
        Export([.. Enumerable.Range(1, 1000).Select(id => State(id))]);
        Execute($"ANALYZE {Log.Events}");

        // act
        var plan = Plan($"SELECT * FROM {Log.Events} WHERE sender_id = {Sender} AND {Log.IdColumn} = 500 ORDER BY id");

        // assert
        Assert.Contains($"using {Log.HistoryIndex} on {Log.Events}", plan);
    }

    /// <summary>
    /// The lookup every end marker runs before it sweeps.
    /// </summary>
    [Theory(DisplayName = "Reading Should find the start marker an end marker sweeps against from the marker index")]
    [EveryEventLog]
    public void Reading_Should_find_the_start_marker_an_end_marker_sweeps_against_from_the_marker_index(EventLog log)
    {
        // arrange, a long log with one resync in it
        Migrate(log);
        Export([.. Enumerable.Range(1, 1000).Select(id => State(id))]);
        Export(ResyncStarted());
        var end = Export(ResyncFinished()).Single();
        Execute($"ANALYZE {Log.Events}");

        // act, the lookup as the trigger runs it
        var plan = Plan($"SELECT max(id) FROM {Log.Events} WHERE sender_id = {Sender} AND event_type = 2 AND id < {end}");

        // assert
        Assert.Contains($"using {Log.MarkerIndex} on {Log.Events}", plan);
    }
}
