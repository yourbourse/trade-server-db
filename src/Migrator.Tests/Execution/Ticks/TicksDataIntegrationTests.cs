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

namespace YB.TradeServer.Db.Migrator.Tests.Execution.Ticks;

/// <summary>
/// What ClickHouse does with ticks under the table's definition: deduplication, partitioning, reading and value limits.
/// </summary>
[Trait("Category", "Integration")]
public class TicksDataIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : TicksTestBase(configBuilder, output)
{
    /// <summary>
    /// At-least-once delivery: the copy collapses only when the parts merge, and FINAL hides it until then.
    /// </summary>
    [Fact(DisplayName = "Workflow Should show a re-sent batch twice until the parts merge and once under FINAL")]
    public void Workflow_Should_show_a_re_sent_batch_twice_until_the_parts_merge_and_once_under_FINAL()
    {
        // arrange, merges held so that "before the merge" is not a race
        var connectionString = Migrate();
        Execute(connectionString, "SYSTEM STOP MERGES ticks");

        Tick[] batch = [new(1, At(0)), new(2, At(1)), new(3, At(2))];

        // act, the confirmation is lost and the rows go again in a later batch
        Insert(connectionString, batch);
        Insert(connectionString, [.. batch.Select(tick => tick with { BatchTime = At(60) })]);

        // assert
        Assert.Equal(6, Count(connectionString, "ticks"));
        Assert.Equal(3, Count(connectionString, "ticks FINAL"));

        // and the merge keeps one copy of each, the one inserted last
        Execute(connectionString, "SYSTEM START MERGES ticks");
        Execute(connectionString, "OPTIMIZE TABLE ticks FINAL");

        Assert.Equal(3, Count(connectionString, "ticks"));
        Assert.Equal([Format(At(60))], Strings(connectionString, "SELECT DISTINCT toString(batch_time) FROM ticks"));
    }

    [Fact(DisplayName = "Workflow Should keep the ticks of two servers apart when their counters and timestamps coincide")]
    public void Workflow_Should_keep_the_ticks_of_two_servers_apart_when_their_counters_and_timestamps_coincide()
    {
        // arrange
        var connectionString = Migrate();

        Tick[] batch = [new(1, At(0)), new(2, At(1)), new(3, At(2))];
        Tick[] otherBatch = [.. batch.Select(tick => tick with { SenderId = OtherSender })];

        // act, both publish the same book under the same numbering, and the other server re-sends
        Insert(connectionString, batch);
        Insert(connectionString, otherBatch);
        Insert(connectionString, otherBatch);

        // assert
        Assert.Equal(["7: 3", "8: 3"], Strings(connectionString,
            "SELECT concat(toString(sender_id), ': ', toString(count())) FROM ticks FINAL GROUP BY sender_id ORDER BY sender_id"));
    }

    /// <summary>
    /// sequence is per server instance, so only created_time reads ticks in time order across a restart.
    /// </summary>
    [Fact(DisplayName = "Workflow Should keep the ticks of a restarted server and read them in time order by created_time")]
    public void Workflow_Should_keep_the_ticks_of_a_restarted_server_and_read_them_in_time_order_by_created_time()
    {
        // arrange
        var connectionString = Migrate();

        // act, three ticks, a restart, then two more numbered from 1 again
        Insert(connectionString, new Tick(1, At(0)), new Tick(2, At(1)), new Tick(3, At(2)));
        Insert(connectionString, new Tick(1, At(10)), new Tick(2, At(11)));
        Execute(connectionString, "OPTIMIZE TABLE ticks FINAL");

        // assert, none collapsed, and created_time gives the publication order
        Assert.Equal([1L, 2L, 3L, 1L, 2L], Longs(connectionString, "SELECT sequence FROM ticks ORDER BY created_time"));

        // while sequence interleaves the two instances
        Assert.Equal(new[] { At(0), At(10), At(1), At(11), At(2) }.Select(Format),
            Strings(connectionString, "SELECT toString(created_time) FROM ticks ORDER BY sequence, created_time"));
    }

    /// <summary>
    /// The close shares created_time with the last price, so only sequence keeps the two apart and orders them.
    /// </summary>
    [Fact(DisplayName = "Workflow Should keep a session close apart from the last price it is stamped with")]
    public void Workflow_Should_keep_a_session_close_apart_from_the_last_price_it_is_stamped_with()
    {
        // arrange
        var connectionString = Migrate();
        var lastPrice = new Tick(2, At(1));

        // act, the close: both sides empty, stamped with the time of the last price update
        Insert(connectionString, new Tick(1, At(0)), lastPrice);
        Insert(connectionString, lastPrice with { Sequence = 3, BidPrice = "0", BidVolume = "0", AskPrice = "0", AskVolume = "0" });
        Execute(connectionString, "OPTIMIZE TABLE ticks FINAL");

        // assert, both survive
        Assert.Equal([1L, 2L, 3L], Sequences(connectionString));

        // and the latest book, ties on created_time broken by sequence, is the empty one
        Assert.Equal("0 0 0 0", Scalar<string>(connectionString, $"SELECT argMax({BookColumns}, (created_time, sequence)) FROM ticks"));
    }

    /// <summary>
    /// Inserts are buffered (async_insert is on by default in 26.x); at-least-once still needs the refusal to come back.
    /// </summary>
    [Fact(DisplayName = "Workflow Should report a refused batch to the exporter itself")]
    public void Workflow_Should_report_a_refused_batch_to_the_exporter_itself()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        // act, assert, the exporter is told, so it keeps the batch and retries rather than moving on
        AssertError(ExporterConnectionString(request),
            InsertSql(new Tick(1, At(0)), new Tick(2, At(1), BidVolume: "10000000000")), ArgumentOutOfBound);

        Assert.Equal(0, Count(request.ConnectionString, "ticks"));
    }

    /// <summary>
    /// No version column: the key alone decides and the last insert wins, which is why sender_id must be unique.
    /// </summary>
    [Fact(DisplayName = "Invariant Should collapse ticks sharing the sorting key whatever else differs")]
    public void Invariant_Should_collapse_ticks_sharing_the_sorting_key_whatever_else_differs()
    {
        // arrange
        var connectionString = Migrate();
        var first = new Tick(1, At(0), SymbolId: 501, BidPrice: "1.1", BidVolume: "1", AskPrice: "1.2", AskVolume: "1");

        // act, the same key with every other column changed
        Insert(connectionString, first);
        Insert(connectionString, first with { BatchTime = At(5), SymbolId = 999, BidPrice = "1.3", BidVolume = "3", AskPrice = "1.4", AskVolume = "3" });

        // assert
        Assert.Equal([$"999 {Format(At(5))} 1.3 3 1.4 3"], Strings(connectionString,
            $"SELECT concatWithSeparator(' ', toString(symbol_id), toString(batch_time), {BookColumns}) FROM ticks FINAL"));
    }

    [Fact(DisplayName = "Invariant Should keep apart ticks that differ in any one column of the sorting key")]
    public void Invariant_Should_keep_apart_ticks_that_differ_in_any_one_column_of_the_sorting_key()
    {
        // arrange
        var connectionString = Migrate();
        var tick = new Tick(100, At(0));

        // act, each variant moves one key column by as little as it can
        Insert(connectionString, tick);
        Insert(connectionString, tick with { SenderId = OtherSender });
        Insert(connectionString, tick with { Symbol = "eurusd" });
        Insert(connectionString, tick with { CreatedTime = At(0).AddTicks(10) }); // one microsecond
        Insert(connectionString, tick with { Sequence = 101 });
        Execute(connectionString, "OPTIMIZE TABLE ticks FINAL");

        // assert
        Assert.Equal(5, Count(connectionString, "ticks"));
    }

    /// <summary>
    /// toYYYYMM reads the column's own UTC zone, so neither batch_time nor the writer's session moves a tick.
    /// </summary>
    [Fact(DisplayName = "Invariant Should partition by the UTC month of created_time, not batch_time")]
    public void Invariant_Should_partition_by_the_UTC_month_of_created_time_not_batch_time()
    {
        // arrange
        var connectionString = Migrate(LongRetentionDays);

        // act, either side of a month end to the microsecond, one tick batched in the next month, all from Tokyo
        Execute(connectionString, $"INSERT INTO ticks ({TickColumns}) SETTINGS session_timezone = 'Asia/Tokyo' VALUES " + Values(
            new Tick(1, Utc("2026-01-31 23:59:59.999999")),
            new Tick(2, Utc("2026-02-01 00:00:00")),
            new Tick(3, Utc("2026-02-28 23:59:59.999999"), BatchTime: Utc("2026-03-01 00:00:00.000001"))));

        // assert
        Assert.Equal(["1: 202601", "2: 202602", "3: 202602"], Strings(connectionString,
            "SELECT concat(toString(sequence), ': ', _partition_id) FROM ticks ORDER BY sequence"));
    }

    /// <summary>
    /// The copy shares its original's created_time and so its partition; merges never cross partitions.
    /// </summary>
    [Fact(DisplayName = "Invariant Should collapse a re-sent tick even when the re-send falls in the next month")]
    public void Invariant_Should_collapse_a_re_sent_tick_even_when_the_re_send_falls_in_the_next_month()
    {
        // arrange
        var connectionString = Migrate(LongRetentionDays);
        var tick = new Tick(1, Utc("2026-01-31 23:59:59.5"), BatchTime: Utc("2026-01-31 23:59:59.9"));

        // act, the confirmation is lost at midnight and the copy goes out in February's first batch
        Insert(connectionString, tick);
        Insert(connectionString, tick with { BatchTime = Utc("2026-02-01 00:00:05") });
        Execute(connectionString, "OPTIMIZE TABLE ticks FINAL");

        // assert, one row on disk, not merely under FINAL, which would hide a copy stranded in another partition
        Assert.Equal(1, Count(connectionString, "ticks"));
    }

    /// <summary>
    /// force_primary_key fails any query the key cannot narrow: filter by symbol name, not by symbol_id.
    /// </summary>
    [Fact(DisplayName = "Reading Should serve the ticks of one symbol from the primary key")]
    public void Reading_Should_serve_the_ticks_of_one_symbol_from_the_primary_key()
    {
        // arrange
        var connectionString = Migrate();
        Insert(connectionString, new Tick(1, At(0)), new Tick(2, At(1), Symbol: "GBPUSD", SymbolId: 2));

        // act, assert
        Assert.Equal(1, Scalar<long>(connectionString,
            $"SELECT count() FROM ticks WHERE sender_id = {Sender} AND short_symbol_name = 'EURUSD' SETTINGS force_primary_key = 1"));

        AssertError(connectionString, "SELECT count() FROM ticks WHERE symbol_id = 1 SETTINGS force_primary_key = 1", IndexNotUsed);
    }

    /// <summary>
    /// force_index_by_date fails any query neither the partition key nor its minmax index narrows.
    /// </summary>
    [Fact(DisplayName = "Reading Should prune partitions by created_time, not batch_time")]
    public void Reading_Should_prune_partitions_by_created_time_not_batch_time()
    {
        // arrange
        var connectionString = Migrate();
        Insert(connectionString, new Tick(1, At(0)));

        // act, assert
        Assert.Equal(1, Scalar<long>(connectionString,
            $"SELECT count() FROM ticks WHERE created_time >= {Time(At(-60))} SETTINGS force_index_by_date = 1"));

        AssertError(connectionString, $"SELECT count() FROM ticks WHERE batch_time >= {Time(At(-60))} SETTINGS force_index_by_date = 1", IndexNotUsed);
    }

    /// <summary>
    /// SCHEMA_REFERENCE offers grouping by the key as the cheaper read; before a merge it has to agree with FINAL.
    /// </summary>
    [Fact(DisplayName = "Reading Should count the same deduplicating by the key in the query as under FINAL")]
    public void Reading_Should_count_the_same_deduplicating_by_the_key_in_the_query_as_under_FINAL()
    {
        // arrange, a batch, its re-send carrying one new tick, and no merge
        var connectionString = Migrate();
        Execute(connectionString, "SYSTEM STOP MERGES ticks");

        Tick[] batch = [new(1, At(0)), new(2, At(1), Symbol: "GBPUSD", SymbolId: 2), new(3, At(2))];

        Insert(connectionString, batch);
        Insert(connectionString, [.. batch.Select(tick => tick with { BatchTime = At(60) }), new Tick(4, At(3))]);

        // act
        var final = Strings(connectionString,
            "SELECT concat(short_symbol_name, ': ', toString(count())) FROM ticks FINAL GROUP BY short_symbol_name ORDER BY short_symbol_name");
        var grouped = Strings(connectionString,
            "SELECT concat(short_symbol_name, ': ', toString(count())) " +
            "FROM (SELECT short_symbol_name FROM ticks GROUP BY sender_id, short_symbol_name, created_time, sequence) " +
            "GROUP BY short_symbol_name ORDER BY short_symbol_name");

        // assert
        Assert.Equal(7, Count(connectionString, "ticks"));
        Assert.Equal(["EURUSD: 3", "GBPUSD: 1"], final);
        Assert.Equal(final, grouped);
    }

    /// <summary>
    /// optimize_on_insert merges the block itself, so the copy never reaches a part.
    /// </summary>
    [Fact(DisplayName = "Edge case Should collapse a tick duplicated within one batch as it is inserted")]
    public void Edge_case_Should_collapse_a_tick_duplicated_within_one_batch_as_it_is_inserted()
    {
        // arrange
        var connectionString = Migrate();
        var tick = new Tick(1, At(0));

        // act
        Insert(connectionString, tick, tick, new Tick(2, At(1)));

        // assert, without FINAL
        Assert.Equal(2, Count(connectionString, "ticks"));
    }

    /// <summary>
    /// The extremes of Decimal(38, 12) and Decimal(18, 8), beyond what a double or a C# decimal holds.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep prices and volumes exact at the full scale and range of their types")]
    public void Edge_case_Should_keep_prices_and_volumes_exact_at_the_full_scale_and_range_of_their_types()
    {
        // arrange
        var connectionString = Migrate();

        // act, the largest and smallest magnitudes, and a negative price as some instruments quote
        Insert(connectionString,
            new Tick(1, At(0),
                BidPrice: "99999999999999999999999999.999999999999", BidVolume: "9999999999.99999999",
                AskPrice: "-99999999999999999999999999.999999999999", AskVolume: "0.00000001"),
            new Tick(2, At(1), BidPrice: "0.000000000001", BidVolume: "1", AskPrice: "-37.63", AskVolume: "1"));

        // assert
        Assert.Equal(
        [
            "99999999999999999999999999.999999999999 9999999999.99999999 -99999999999999999999999999.999999999999 0.00000001",
            "0.000000000001 1 -37.63 1"
        ], Books(connectionString));
    }

    /// <summary>
    /// Rounding is the exporter's job: the server cuts the extra digits, so 1.9999999999999 does not become 2.
    /// </summary>
    [Fact(DisplayName = "Edge case Should cut digits beyond the column scale rather than round them")]
    public void Edge_case_Should_cut_digits_beyond_the_column_scale_rather_than_round_them()
    {
        // arrange
        var connectionString = Migrate();

        // act, one digit past the scale of each column
        Insert(connectionString,
            new Tick(1, At(0), BidPrice: "1.9999999999999", BidVolume: "0.999999999", AskPrice: "1.1234567890129", AskVolume: "1.123456789"));

        // assert
        Assert.Equal(["1.999999999999 0.99999999 1.123456789012 1.12345678"], Books(connectionString));
    }

    /// <summary>
    /// One out-of-range value rejects the batch: nothing of it is stored.
    /// </summary>
    [Fact(DisplayName = "Edge case Should refuse the whole batch when one value overflows its column")]
    public void Edge_case_Should_refuse_the_whole_batch_when_one_value_overflows_its_column()
    {
        // arrange
        var connectionString = Migrate();

        // act, assert, a volume needing 11 integer digits mid-batch, then a price needing 27
        AssertError(connectionString,
            InsertSql(new Tick(1, At(0)), new Tick(2, At(1), BidVolume: "10000000000"), new Tick(3, At(2))), ArgumentOutOfBound);
        AssertError(connectionString,
            InsertSql(new Tick(4, At(3), AskPrice: "100000000000000000000000000")), ArgumentOutOfBound);

        Assert.Equal(0, Count(connectionString, "ticks"));
    }

    /// <summary>
    /// Decimal has no NaN or infinity, which is why an empty side is written as zeros.
    /// </summary>
    [Theory(DisplayName = "Edge case Should refuse a price that is not a number")]
    [InlineData("nan")]
    [InlineData("inf")]
    [InlineData("-inf")]
    public void Edge_case_Should_refuse_a_price_that_is_not_a_number(string price)
    {
        // arrange
        var connectionString = Migrate();

        // act, assert
        AssertError(connectionString, InsertSql(new Tick(1, At(0), BidPrice: price)), DecimalOverflow);

        Assert.Equal(0, Count(connectionString, "ticks"));
    }

    /// <summary>
    /// NOT NULL refuses nothing here: input_format_null_as_default stores the type's zero instead.
    /// </summary>
    [Fact(DisplayName = "Edge case Should store a NULL price as zero, indistinguishable from an empty side")]
    public void Edge_case_Should_store_a_NULL_price_as_zero_indistinguishable_from_an_empty_side()
    {
        // arrange
        var connectionString = Migrate();

        // act
        Insert(connectionString, new Tick(1, At(0), BidPrice: "NULL", BidVolume: "NULL"));

        // assert
        Assert.Equal(["0 0 1.0856 500000"], Books(connectionString));
    }

    [Fact(DisplayName = "Edge case Should hold the full UInt64 range in the id columns")]
    public void Edge_case_Should_hold_the_full_UInt64_range_in_the_id_columns()
    {
        // arrange
        var connectionString = Migrate();

        // act
        Insert(connectionString, new Tick(ulong.MaxValue, At(0), SenderId: ulong.MaxValue, SymbolId: ulong.MaxValue));

        // assert
        Assert.Equal(["18446744073709551615 18446744073709551615 18446744073709551615"], Strings(connectionString,
            "SELECT concatWithSeparator(' ', toString(sender_id), toString(sequence), toString(symbol_id)) FROM ticks"));
    }

    /// <summary>
    /// The schema cannot catch a signed or overflowing id: the literal is stored wrapped.
    /// </summary>
    [Fact(DisplayName = "Edge case Should wrap an id outside the UInt64 range rather than refuse it")]
    public void Edge_case_Should_wrap_an_id_outside_the_UInt64_range_rather_than_refuse_it()
    {
        // arrange
        var connectionString = Migrate();

        // act, literals a ulong cannot carry: -1 and 2^64
        Execute(connectionString,
            $"INSERT INTO ticks ({TickColumns}) VALUES (-1, 18446744073709551616, {Time(At(0))}, {Time(At(0))}, 1, 'EURUSD', 1, 1, 1, 1)");

        // assert
        Assert.Equal(["18446744073709551615 0"], Strings(connectionString,
            "SELECT concatWithSeparator(' ', toString(sender_id), toString(sequence)) FROM ticks"));
    }

    [Fact(DisplayName = "Edge case Should keep timestamps to the microsecond in UTC whatever the session time zone")]
    public void Edge_case_Should_keep_timestamps_to_the_microsecond_in_UTC_whatever_the_session_time_zone()
    {
        // arrange
        var connectionString = Migrate(LongRetentionDays);

        // act, written from Tokyo with a seventh fractional digit, which is cut rather than rounded into February
        Execute(connectionString,
            $"INSERT INTO ticks ({TickColumns}) SETTINGS session_timezone = 'Asia/Tokyo' VALUES " +
            "(7, 1, '2026-01-31 23:59:59.9999999', '2026-01-31 23:59:59.9999999', 1, 'EURUSD', 1, 1, 1, 1)");

        // assert, the same instant read back from Tokyo and as an epoch
        Assert.Equal("2026-01-31 23:59:59.999999",
            Scalar<string>(connectionString, "SELECT toString(created_time) FROM ticks SETTINGS session_timezone = 'Asia/Tokyo'"));
        Assert.Equal(1769903999999999L, Scalar<long>(connectionString, "SELECT toUnixTimestamp64Micro(created_time) FROM ticks"));
    }

    [Fact(DisplayName = "Edge case Should keep symbol names with punctuation, spaces and non-ASCII characters intact")]
    public void Edge_case_Should_keep_symbol_names_with_punctuation_spaces_and_non_ASCII_characters_intact()
    {
        // arrange
        var connectionString = Migrate();
        string[] names = ["BRK.B", "XAU/USD#", "US30 Cash", "DE40 €", "日経225"];

        // act
        Insert(connectionString, [.. names.Select((name, index) => new Tick((ulong)index + 1, At(index), Symbol: name))]);

        // assert
        Assert.Equal(names, Strings(connectionString, "SELECT short_symbol_name FROM ticks ORDER BY sequence"));
    }

    /// <summary>
    /// Past low_cardinality_max_dictionary_size (8192) the extra keys bypass the shared dictionary; nothing fails.
    /// </summary>
    [Fact(DisplayName = "Edge case Should store more distinct symbols than one LowCardinality dictionary holds")]
    public void Edge_case_Should_store_more_distinct_symbols_than_one_LowCardinality_dictionary_holds()
    {
        // arrange
        var connectionString = Migrate();

        // act
        Execute(connectionString,
            $"INSERT INTO ticks ({TickColumns}) " +
            $"SELECT {Sender}, number, now64(6), now64(6), number, concat('SYM', toString(number)), 1, 1, 1, 1 FROM numbers(10000)");

        // assert
        Assert.Equal(10000, Scalar<long>(connectionString, "SELECT uniqExact(short_symbol_name) FROM ticks"));
    }

    /// <summary>
    /// A zero or omitted created_time reads as 1970: the insert succeeds, then the retention deletes it without a word.
    /// </summary>
    [Fact(DisplayName = "Edge case Should accept a tick stamped at the epoch and then drop it as expired")]
    public void Edge_case_Should_accept_a_tick_stamped_at_the_epoch_and_then_drop_it_as_expired()
    {
        // arrange
        var connectionString = Migrate();

        // act, a zero timestamp, a batch that leaves created_time out, and a sound tick; none of them is refused
        Insert(connectionString, new Tick(1, DateTime.UnixEpoch, BatchTime: At(0)));
        Execute(connectionString,
            "INSERT INTO ticks (sender_id, sequence, batch_time, symbol_id, short_symbol_name, bid_price, bid_volume, ask_price, ask_volume) " +
            $"VALUES ({Sender}, 2, {Time(At(0))}, 1, 'EURUSD', 1, 1, 1, 1)");
        Insert(connectionString, new Tick(3, At(0)));

        ApplyRetention(connectionString);

        // assert
        Assert.Equal([3L], Sequences(connectionString));
    }
}
