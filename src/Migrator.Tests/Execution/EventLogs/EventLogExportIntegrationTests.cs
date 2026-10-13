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
/// What each log takes from the exporter and what it refuses: numbering, defaults, NULLs, widths, text, whole batches,
/// the export cursor, two senders at once and a batch of ten thousand.
/// </summary>
[Trait("Category", "Integration")]
public class EventLogExportIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    /// <summary>
    /// SQLSTATE 428C9, which PostgresErrorCodes does not name.
    /// </summary>
    private const string GeneratedAlways = "428C9";

    [Theory(DisplayName = "Export Should number a batch in the order it was sent and stamp it with one arrival time")]
    [EveryEventLog]
    public void Export_Should_number_a_batch_in_the_order_it_was_sent_and_stamp_it_with_one_arrival_time(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, two batches
        var first = Export(State(1), Deleted(2), ResyncStarted(), Event(99));
        var second = Export(State(3));

        // assert, the log reads back in the order sent
        Assert.Equal(["state 1", "deletion 2", "resync started", "type 99", "state 3"], EventsInLogOrder());
        Assert.True(second.Single() > first[^1]);

        // and a batch shares one time_inserted, so only the id orders events within it
        Assert.Equal(1, Scalar<long>($"SELECT count(DISTINCT time_inserted) FROM {Log.Events} WHERE id <= {first[^1]}"));
    }

    /// <summary>
    /// COPY writes an identity column as given, unlike INSERT, so only the exporter leaving id out keeps the log in order.
    /// </summary>
    [Theory(DisplayName = "Edge case Should take an id a COPY supplies, which an INSERT would refuse")]
    [EveryEventLog]
    public void Edge_case_Should_take_an_id_a_COPY_supplies_which_an_INSERT_would_refuse(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, an INSERT naming id is refused
        AssertRefused(GeneratedAlways, () => Execute(Exporter, $"INSERT INTO {Log.Events} (id, sender_id) VALUES (1000, {Sender})"));

        // while a COPY naming it is taken at its word, and the log goes on numbering from 1
        ExportWithId(1000, State(1, label: "sent with an id"));
        Export(State(2, label: "numbered by the log"));

        // assert
        Assert.Equal(["7/1 v1 sent with an id #1000", "7/2 v1 numbered by the log #1"], RowsWithStamps());

        // and the next sweep, which compares ids, takes the event for one that arrived after its start marker
        Export(ResyncStarted());
        Export(ResyncFinished());

        Assert.Equal(["7/1 v1 sent with an id"], Rows());
    }

    [Theory(DisplayName = "Export Should store a text column the exporter leaves out as empty, and refuse any other left out")]
    [EveryEventLog]
    public void Export_Should_store_a_text_column_the_exporter_leaves_out_as_empty_and_refuse_any_other_left_out(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, a COPY naming every column but the label
        ExportLeavingOut(Log.LabelColumn, State(1, label: "never sent"));

        // assert
        Assert.Equal(["7/1 v1"], Rows());

        // and one leaving out the version is refused before anything reaches the table
        AssertRefused(PostgresErrorCodes.NotNullViolation, () => ExportLeavingOut("version", State(2)));

        Assert.Equal(["7/1 v1"], Rows());
    }

    [Theory(DisplayName = "Export Should refuse a NULL in any column that takes none, and the whole batch with it")]
    [EveryEventLog]
    public void Export_Should_refuse_a_NULL_in_any_column_that_takes_none_and_the_whole_batch_with_it(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1, label: "before"));

        // act, assert, a sound event followed by one with a NULL, once for every column
        foreach (var column in LogColumns.Where(column => !column.Nullable))
        {
            AssertRefused(PostgresErrorCodes.NotNullViolation, () => Export(State(2), State(3).With(column.Name, null)), column.Name);
        }

        // and nothing of any of those batches was stored or projected
        Assert.Equal(1, Count(Log.Events));
        Assert.Equal(["7/1 v1 before"], Rows());
    }

    [Theory(DisplayName = "Export Should refuse text longer than its column, and the whole batch with it")]
    [EveryEventLog]
    public void Export_Should_refuse_text_longer_than_its_column_and_the_whole_batch_with_it(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1, label: "before"));

        // act, assert, a sound event followed by one character too many, once for every text column
        foreach (var column in LogColumns.Where(column => column.Width is not null))
        {
            AssertRefused(PostgresErrorCodes.StringDataRightTruncation,
                () => Export(State(2), State(3).With(column.Name, new string('x', column.Width!.Value + 1))), column.Name);
        }

        // and nothing of any of those batches was stored or projected
        Assert.Equal(1, Count(Log.Events));
        Assert.Equal(["7/1 v1 before"], Rows());
    }

    /// <summary>
    /// VARCHAR(n) counts characters, not bytes, so a width holds as many euro signs as letters.
    /// </summary>
    [Theory(DisplayName = "Edge case Should hold text at the full width of every text column, counted in characters")]
    [EveryEventLog]
    public void Edge_case_Should_hold_text_at_the_full_width_of_every_text_column_counted_in_characters(EventLog log)
    {
        // arrange
        Migrate(log);
        var textColumns = LogColumns.Where(column => column.Width is not null).ToList();

        // act, every text column filled to its width with a three-byte character
        Export(textColumns.Aggregate(State(1), (state, column) => state.With(column.Name, new string('€', column.Width!.Value))));

        // assert, the derived row holds every one of them whole
        var lengths = string.Join(", ", textColumns.Select(column => $"'{column.Name}: ' || char_length({column.Name})"));

        Assert.Equal(textColumns.Select(column => $"{column.Name}: {column.Width}"), Strings($"SELECT unnest(ARRAY[{lengths}]) FROM {Log.State}"));
        Assert.Empty(Mismatches());
    }

    [Theory(DisplayName = "Edge case Should keep quotes, backslashes, tabs, line breaks and non-ASCII text as sent")]
    [EveryEventLog]
    public void Edge_case_Should_keep_quotes_backslashes_tabs_line_breaks_and_non_ASCII_text_as_sent(EventLog log)
    {
        // arrange
        Migrate(log);
        const string awkward = "O'Brien \\ \"quoted\"\ttabbed\r\nsecond line, Zürich 日本 €";

        // act
        Export(State(1, label: awkward));

        // assert
        Assert.Equal([awkward], Strings($"SELECT {Log.LabelColumn} FROM {Log.Events}"));
        Assert.Equal([awkward], Strings($"SELECT {Log.LabelColumn} FROM {Log.State}"));
    }

    [Theory(DisplayName = "Edge case Should hold senders, ids and versions up to the BIGINT maximum")]
    [EveryEventLog]
    public void Edge_case_Should_hold_senders_ids_and_versions_up_to_the_BIGINT_maximum(EventLog log)
    {
        // arrange
        Migrate(log);

        // act
        Export(State(long.MaxValue, version: long.MaxValue, sender: long.MaxValue));

        // assert
        Assert.Equal([$"{long.MaxValue}/{long.MaxValue} v{long.MaxValue}"], Rows());
    }

    /// <summary>
    /// No lag and no drift: the trigger writes in the batch's own transaction, which also moves the export cursor.
    /// </summary>
    [Theory(DisplayName = "Workflow Should project inside the batch transaction, and commit or roll back with it and its cursor")]
    [EveryEventLog]
    public void Workflow_Should_project_inside_the_batch_transaction_and_commit_or_roll_back_with_it_and_its_cursor(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, a batch the exporter projects and then loses before it commits
        using (var exporter = Open(Exporter))
        using (var batch = exporter.BeginTransaction())
        {
            Copy(exporter, State(1), State(2));
            MoveCursor(exporter, to: 2);

            Assert.Equal(2, Count(exporter, Log.State));

            batch.Rollback();
        }

        // assert, the log, the derived table and the cursor are as they were
        Assert.Equal(0, Count(Log.Events));
        Assert.Empty(Rows());
        Assert.Empty(Cursor());

        // and the same batch committed moves all three
        using (var exporter = Open(Exporter))
        using (var batch = exporter.BeginTransaction())
        {
            Copy(exporter, State(1), State(2));
            MoveCursor(exporter, to: 2);

            batch.Commit();
        }

        Assert.Equal(2, Count(Log.Events));
        Assert.Equal(["7/1 v1", "7/2 v1"], Rows());
        Assert.Equal([$"7 {Log.Events} 2"], Cursor());
    }

    /// <summary>
    /// A sweep that reached past its own sender would lock the other sender's rows until it committed.
    /// </summary>
    [Theory(DisplayName = "Workflow Should not keep one sender waiting on the open batch of another")]
    [EveryEventLog]
    public void Workflow_Should_not_keep_one_sender_waiting_on_the_open_batch_of_another(EventLog log)
    {
        // arrange, the same id live on both senders
        Migrate(log);
        Export(State(1), State(1, sender: OtherSender));

        // act, sender 7 sweeps its entity in a batch that stays open
        using var slow = Open(Exporter);
        using var batch = slow.BeginTransaction();
        Copy(slow, ResyncStarted(), ResyncFinished());

        // while sender 8 changes its own, giving up on any lock it waits two seconds for
        using (var quick = Open(Exporter))
        {
            Execute(quick, "SET lock_timeout = '2s'");
            Copy(quick, State(1, version: 2, sender: OtherSender));
        }

        batch.Commit();

        // assert
        Assert.Equal(["8/1 v2"], Rows());
    }

    [Theory(DisplayName = "Workflow Should project a resync of ten thousand entities sent as one batch")]
    [EveryEventLog]
    public void Workflow_Should_project_a_resync_of_ten_thousand_entities_sent_as_one_batch(EventLog log)
    {
        // arrange, ten thousand live entities
        Migrate(log);
        Export([.. Enumerable.Range(1, 10_000).Select(id => State(id))]);

        // act, one batch: the start marker, every even id re-announced at version 2, the end marker
        Export([ResyncStarted(), .. Enumerable.Range(1, 10_000).Where(id => id % 2 == 0).Select(id => State(id, version: 2)), ResyncFinished()]);

        // assert, the odd half is swept
        Assert.Equal(5_000, Count(Log.State));
        Assert.Equal(5_000, Scalar<long>($"SELECT count(*) FROM {Log.State} WHERE version = 2 AND {Log.IdColumn} % 2 = 0"));
    }

    /// <summary>
    /// A COPY that names the id column, which the exporter never should.
    /// </summary>
    private void ExportWithId(long id, LogEvent logEvent)
    {
        using var exporter = Open(Exporter);
        using var writer = exporter.BeginTextImport($"COPY {Log.Events} (id, {CopyColumns()}) FROM STDIN");

        writer.Write($"{id}\t{CopyLine(logEvent)}\n");
    }

    /// <summary>
    /// Records how far the sender's export of this log has got, as the exporter does in each batch.
    /// </summary>
    private void MoveCursor(NpgsqlConnection exporter, long to)
    {
        Execute(exporter, $"""
                           INSERT INTO export_cursor (sender_id, table_name, last_written_position)
                           VALUES ({Sender}, '{Log.Events}', {to})
                           ON CONFLICT (sender_id, table_name) DO UPDATE SET last_written_position = excluded.last_written_position
                           """);
    }

    private List<string> Cursor()
    {
        return Strings("SELECT sender_id || ' ' || table_name || ' ' || last_written_position FROM export_cursor ORDER BY sender_id, table_name");
    }
}
