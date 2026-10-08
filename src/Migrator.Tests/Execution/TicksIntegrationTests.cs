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

using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using DbUp.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Extensions;
using YB.TradeServer.Db.Migrator.Handlers;

namespace YB.TradeServer.Db.Migrator.Tests.Execution;

/// <summary>
/// The ClickHouse ticks table: schema, deduplication, retention, value limits, reading and grants.
/// </summary>
/// <remarks>
/// Pins ClickHouse's behaviour under our DDL, as verified on 26.7. Each test gets its own database, users and roles.
/// </remarks>
[Trait("Category", "Integration")]
public class TicksIntegrationTests : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    private const ulong Sender = 7;
    private const ulong OtherSender = 8;

    private const string TickColumns =
        "sender_id, sequence, batch_time, created_time, symbol_id, short_symbol_name, bid_price, bid_volume, ask_price, ask_volume";

    /// <summary>
    /// The engine clause the migration leaves on the table, as system.tables prints it.
    /// </summary>
    private const string AgreedEngine =
        "ReplacingMergeTree PARTITION BY toYYYYMM(created_time) PRIMARY KEY (sender_id, short_symbol_name) " +
        "ORDER BY (sender_id, short_symbol_name, created_time, sequence) TTL toDateTime(created_time) + toIntervalDay(31) " +
        "SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1";

    /// <summary>
    /// A tick's book as text: bid price, bid volume, ask price, ask volume.
    /// </summary>
    private const string BookColumns =
        "concatWithSeparator(' ', toString(bid_price), toString(bid_volume), toString(ask_price), toString(ask_volume))";

    /// <summary>
    /// Keeps the fixed 2026 dates live, well short of the ~29,000 days at which the TTL expression overflows DateTime.
    /// </summary>
    private const int LongRetentionDays = 20000;

    private const int UnknownTable = 60;
    private const int ArgumentOutOfBound = 69;
    private const int IllegalFinal = 181;
    private const int IndexNotUsed = 277;
    private const int DecimalOverflow = 407;
    private const int AccessDenied = 497;
    private const int AuthenticationFailed = 516;

    private readonly ConfigBuilder configBuilder;
    private readonly ITestOutputHelper output;
    private readonly List<(string ConnectionString, string DatabaseName)> provisionedDatabases = [];

    /// <summary>
    /// The instant a test's ticks are stamped around: an hour back, in whole seconds, well inside the retention.
    /// </summary>
    private readonly DateTime recent;

    public TicksIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output)
    {
        this.configBuilder = configBuilder;
        this.output = output;

        var now = DateTime.UtcNow;
        recent = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddHours(-1);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (connectionString, databaseName) in provisionedDatabases)
        {
            try
            {
                await using var connection = new ClickHouseConnection(WithDatabase(connectionString, "system"));
                await connection.OpenAsync();

                // users and roles are server wide, so dropping the database would leave them behind
                string[] statements =
                [
                    $"DROP DATABASE IF EXISTS {databaseName}",
                    $"DROP USER IF EXISTS {databaseName}_exporter, {databaseName}_reader",
                    $"DROP ROLE IF EXISTS role_{databaseName}_exporter, role_{databaseName}_ro"
                ];

                foreach (var statement in statements)
                {
                    await using var command = connection.CreateCommand(statement);
                    await command.ExecuteNonQueryAsync();
                }
            }
            catch (Exception e)
            {
                output.WriteLine($"Unable to drop {databaseName}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// The guide's columns written out: fails on a reorder, a retype, a codec change, a default or a Nullable.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the agreed columns, types and codecs in the agreed order")]
    public void Schema_Should_carry_the_agreed_columns_types_and_codecs_in_the_agreed_order()
    {
        // arrange
        var connectionString = Migrate();

        string[] agreed =
        [
            "sender_id UInt64",
            "sequence UInt64",
            "batch_time DateTime64(6, 'UTC') CODEC(Delta(8), Default)",
            "created_time DateTime64(6, 'UTC') CODEC(Delta(8), Default)",
            "symbol_id UInt64",
            "short_symbol_name LowCardinality(String)",
            "bid_price Decimal(38, 12)",
            "bid_volume Decimal(18, 8)",
            "ask_price Decimal(38, 12)",
            "ask_volume Decimal(18, 8)"
        ];

        // act, a default expression would trail its column
        var actual = Strings(connectionString,
            """
            SELECT arrayStringConcat(arrayFilter(part -> part != '', [name, type, compression_codec, default_kind, default_expression]), ' ')
            FROM system.columns
            WHERE database = currentDatabase() AND table = 'ticks'
            ORDER BY position
            """);

        // assert
        Assert.Equal(agreed, actual);
    }

    /// <summary>
    /// The engine clause in full: deduplication, partitioning, both keys and the default 31-day retention.
    /// </summary>
    [Fact(DisplayName = "Schema Should use the agreed engine, keys, retention and settings")]
    public void Schema_Should_use_the_agreed_engine_keys_retention_and_settings()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.Equal(AgreedEngine, EngineFull(connectionString, "ticks"));
    }

    /// <summary>
    /// No cursor table and no view: a ClickHouse pipeline keeps its position on disk and only inserts.
    /// </summary>
    [Fact(DisplayName = "Schema Should create the ticks table and the journal and nothing else")]
    public void Schema_Should_create_the_ticks_table_and_the_journal_and_nothing_else()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.Equal(["schema_versions", "ticks"], Tables(connectionString));
    }

    /// <summary>
    /// The previous retention entry is deleted before the new one is written, so exactly one survives.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep one journal entry per ticks script across a retention change")]
    public void Migration_Should_keep_one_journal_entry_per_ticks_script_across_a_retention_change()
    {
        // arrange
        const string retentionEntry = @"^\[clickhouse/post\] ticks_ttl\.sql::[0-9a-f]{32}$";

        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var before = TicksJournalEntries(request.ConnectionString);

        // act
        AssertSuccessful(CreateRunner().Run(WithRetention(request, 7)));

        // assert, the create script once and untouched, the retention script once under a new hash
        var after = TicksJournalEntries(request.ConnectionString);

        Assert.Collection(before,
            retention => Assert.Matches(retentionEntry, retention),
            create => Assert.Equal("[clickhouse] 20260922_01_create_ticks.sql", create));

        Assert.Collection(after,
            retention => Assert.Matches(retentionEntry, retention),
            create => Assert.Equal(before[1], create));

        Assert.NotEqual(before[0], after[0]);
    }

    /// <summary>
    /// A forced run re-applies the retention and the grants over live data; neither may touch a tick.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep the stored ticks through a re-run and a forced re-run")]
    public void Migration_Should_keep_the_stored_ticks_through_a_re_run_and_a_forced_re_run()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, new Tick(1, At(0)), new Tick(2, At(1), BidPrice: "1.0857"), new Tick(3, At(2), AskVolume: "0"));
        var stored = Books(request.ConnectionString);

        // act
        AssertSuccessful(CreateRunner().Run(request));
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Equal(stored, Books(request.ConnectionString));

        // and the exporter still writes after its role was revoked and granted again
        Insert(ExporterConnectionString(request), new Tick(4, At(3)));

        Assert.Equal([1L, 2L, 3L, 4L], Sequences(request.ConnectionString));
    }

    [Fact(DisplayName = "Migration Should create, retain and grant a renamed ticks table")]
    public void Migration_Should_create_retain_and_grant_a_renamed_ticks_table()
    {
        // arrange
        var request = CreateRequest();
        request.Variables["ticks_table"] = "tick_history";

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert
        Assert.Equal(["schema_versions", "tick_history"], Tables(request.ConnectionString));
        Assert.Equal(AgreedEngine, EngineFull(request.ConnectionString, "tick_history"));

        Execute(ExporterConnectionString(request), InsertInto("tick_history", new Tick(1, At(0))));

        Assert.Equal(1, Scalar<long>(ReaderConnectionString(request), "SELECT count() FROM tick_history"));
    }

    /// <summary>
    /// The create script is journalled by name and never runs again, so a forgotten ticks_table fails the run.
    /// </summary>
    [Fact(DisplayName = "Migration Should fail rather than fork a second table when a renamed ticks table is not passed again")]
    public void Migration_Should_fail_rather_than_fork_a_second_table_when_a_renamed_ticks_table_is_not_passed_again()
    {
        // arrange, a deployment that exports into tick_history
        var request = CreateRequest();
        request.Variables["ticks_table"] = "tick_history";
        AssertSuccessful(CreateRunner().Run(request));

        Execute(request.ConnectionString, InsertInto("tick_history", new Tick(1, At(0))));

        // act, a run without the variable
        var forgotten = CreateRunner().Run(request with { Variables = Variables(request.DatabaseName, null) });

        // assert, the retention script trips over the default name; no second table, no tick lost
        var (phase, failed) = forgotten[^1];

        Assert.Equal("post", phase);
        Assert.False(failed.Successful);
        Assert.StartsWith("[clickhouse/post] ticks_ttl.sql::", failed.ErrorScript?.Name);
        Assert.Equal(UnknownTable, Assert.IsType<ClickHouseServerException>(failed.Error).ErrorCode);

        Assert.Equal(["schema_versions", "tick_history"], Tables(request.ConnectionString));
        Assert.Equal(1, Count(request.ConnectionString, "tick_history"));

        // and the corrected run succeeds, but the users phase had already granted on the default name
        AssertSuccessful(CreateRunner().Run(request));

        Assert.Equal(["INSERT ON tick_history", "INSERT ON ticks"], RoleGrants(request.ConnectionString, $"role_{request.DatabaseName}_exporter"));
        Assert.Equal(["SELECT ON tick_history", "SELECT ON ticks"], RoleGrants(request.ConnectionString, $"role_{request.DatabaseName}_ro"));
    }

    /// <summary>
    /// A dry run may not create the database, so the journal is unreadable and every script is reported pending.
    /// </summary>
    [Fact(DisplayName = "Migration Should list the ticks scripts on a dry run and create nothing")]
    public void Migration_Should_list_the_ticks_scripts_on_a_dry_run_and_create_nothing()
    {
        // arrange
        var request = CreateRequest();
        var system = WithDatabase(request.ConnectionString, "system");

        // act
        var dryRun = CreateRunner().Run(request with { DryRun = true });

        // assert, every script in phase order, hashes aside
        AssertSuccessful(dryRun);

        Assert.Equal(
        [
            "[clickhouse] 20260922_01_create_ticks.sql",
            $"[clickhouse/users] exporter.sql for {request.DatabaseName}_exporter",
            $"[clickhouse/users] ro.sql for {request.DatabaseName}_reader",
            "[clickhouse/post] run_on_every_build.sql",
            "[clickhouse/post] ticks_ttl.sql"
        ], dryRun.SelectMany(step => step.Result.Scripts).Select(script => script.Name.Split("::")[0]));

        // and neither the database nor an account exists
        Assert.Equal(0, Scalar<long>(system, $"SELECT count() FROM system.databases WHERE name = '{request.DatabaseName}'"));
        Assert.Equal(0, Scalar<long>(system, $"SELECT count() FROM system.users WHERE startsWith(name, '{request.DatabaseName}')"));
    }

    /// <summary>
    /// A database provisioned by hand from the guide before this repository existed, ticks and all.
    /// </summary>
    [Fact(DisplayName = "Migration Should adopt a ticks table created by hand from the guide")]
    public void Migration_Should_adopt_a_ticks_table_created_by_hand_from_the_guide()
    {
        // arrange
        var request = CreateRequest();
        CreateDatabase(request);
        Execute(request.ConnectionString, HandMadeTicksTable("ReplacingMergeTree"));
        Insert(request.ConnectionString, new Tick(1, At(0)));

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert, the tick survives and the table converges on the one the migration would have made
        Assert.Equal([1L], Sequences(request.ConnectionString));
        Assert.Equal(AgreedEngine, EngineFull(request.ConnectionString, "ticks"));

        Insert(ExporterConnectionString(request), new Tick(2, At(1)));

        Assert.Equal([1L, 2L], Sequences(request.ConnectionString));
    }

    /// <summary>
    /// IF NOT EXISTS adopts what it finds: a plain MergeTree stays one, and FINAL then fails on it.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep the engine of a ticks table it did not create")]
    public void Migration_Should_keep_the_engine_of_a_ticks_table_it_did_not_create()
    {
        // arrange, made by hand on an engine that never deduplicates
        var request = CreateRequest();
        CreateDatabase(request);
        Execute(request.ConnectionString, HandMadeTicksTable("MergeTree"));

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert, the retention is laid on top, the engine is not converted
        Assert.StartsWith("MergeTree PARTITION BY toYYYYMM(created_time)", EngineFull(request.ConnectionString, "ticks"));
        Assert.Contains("TTL toDateTime(created_time) + toIntervalDay(31)", EngineFull(request.ConnectionString, "ticks"));

        AssertError(request.ConnectionString, "SELECT count() FROM ticks FINAL", IllegalFinal);
    }

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

    [Fact(DisplayName = "Retention Should drop a month of ticks once every one of them is older than the retention")]
    public void Retention_Should_drop_a_month_of_ticks_once_every_one_of_them_is_older_than_the_retention()
    {
        // arrange, the default 31 days; 60 days back is always another month than an hour ago
        var connectionString = Migrate();

        Insert(connectionString, new Tick(1, DaysAgo(60)), new Tick(2, DaysAgo(60).AddMinutes(1)));
        Insert(connectionString, new Tick(3, HoursAgo(1)));

        // act
        ApplyRetention(connectionString);

        // assert
        Assert.Equal([3L], Sequences(connectionString));
    }

    [Fact(DisplayName = "Retention Should age a tick by created_time, not batch_time")]
    public void Retention_Should_age_a_tick_by_created_time_not_batch_time()
    {
        // arrange
        var connectionString = Migrate();

        // act, one created long ago and batched now, one created now and batched long ago
        Insert(connectionString, new Tick(1, DaysAgo(60), BatchTime: HoursAgo(1)));
        Insert(connectionString, new Tick(2, HoursAgo(1), BatchTime: DaysAgo(60)));
        ApplyRetention(connectionString);

        // assert
        Assert.Equal([2L], Sequences(connectionString));
    }

    /// <summary>
    /// The cost of ttl_only_drop_parts: retention is a minimum, and only a merge rewriting the part trims it.
    /// </summary>
    [Fact(DisplayName = "Retention Should keep an expired tick while its part still holds a live one")]
    public void Retention_Should_keep_an_expired_tick_while_its_part_still_holds_a_live_one()
    {
        // arrange, a retention cutting mid last month, so one batch straddles the cut inside one partition
        var (cutOff, days) = CutOffMidLastMonth();
        var connectionString = Migrate(days);

        Insert(connectionString, new Tick(1, cutOff.AddDays(-2)), new Tick(2, cutOff.AddDays(2)));

        // act
        ApplyRetention(connectionString);

        // assert, the TTL pass drops parts only whole
        Assert.Equal([1L, 2L], Sequences(connectionString));

        // and a merge that rewrites the part anyway drops the expired row
        Execute(connectionString, "OPTIMIZE TABLE ticks FINAL");

        Assert.Equal([2L], Sequences(connectionString));
    }

    /// <summary>
    /// MODIFY TTL waits for the TTL pass it starts, so the re-run alone removes what the new retention expires.
    /// </summary>
    [Fact(DisplayName = "Retention Should apply a shortened retention to the ticks already stored when the migration re-runs")]
    public void Retention_Should_apply_a_shortened_retention_to_the_ticks_already_stored_when_the_migration_re_runs()
    {
        // arrange, 60 days kept; 35 days back is always another month than an hour ago
        var request = CreateRequest(ticksTtlDays: 60);
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, new Tick(1, DaysAgo(35)));
        Insert(request.ConnectionString, new Tick(2, HoursAgo(1)));
        ApplyRetention(request.ConnectionString);

        Assert.Equal([1L, 2L], Sequences(request.ConnectionString));

        // act
        AssertSuccessful(CreateRunner().Run(WithRetention(request, 7)));

        // assert
        Assert.Equal([2L], Sequences(request.ConnectionString));
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

    /// <summary>
    /// The production path: the exporter's own account, which may insert and do nothing else.
    /// </summary>
    [Fact(DisplayName = "Grants Should let the exporter insert ticks and do nothing else to them")]
    public void Grants_Should_let_the_exporter_insert_ticks_and_do_nothing_else_to_them()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var exporter = ExporterConnectionString(request);

        // act
        Insert(exporter, new Tick(1, At(0)), new Tick(2, At(1)));

        // assert
        Assert.Equal(2, Count(request.ConnectionString, "ticks"));

        AssertDenied(exporter, "SELECT count() FROM ticks");
        AssertDenied(exporter, "SELECT count() FROM ticks FINAL");
        AssertDenied(exporter, "ALTER TABLE ticks DELETE WHERE 1 = 1");
        AssertDenied(exporter, "DELETE FROM ticks WHERE 1 = 1");
        AssertDenied(exporter, "TRUNCATE TABLE ticks");
        AssertDenied(exporter, "OPTIMIZE TABLE ticks FINAL");
        AssertDenied(exporter, "ALTER TABLE ticks MODIFY TTL toDateTime(created_time) + INTERVAL 1 DAY");
        AssertDenied(exporter, "DROP TABLE ticks");

        Assert.Equal(2, Count(request.ConnectionString, "ticks"));
    }

    [Fact(DisplayName = "Grants Should let the reader read ticks and do nothing else to them")]
    public void Grants_Should_let_the_reader_read_ticks_and_do_nothing_else_to_them()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        Insert(request.ConnectionString, new Tick(1, At(0)));

        var reader = ReaderConnectionString(request);

        // act, assert
        Assert.Equal(1, Count(reader, "ticks"));
        Assert.Equal(1, Count(reader, "ticks FINAL"));

        AssertDenied(reader, InsertSql(new Tick(2, At(1))));
        AssertDenied(reader, "ALTER TABLE ticks DELETE WHERE 1 = 1");
        AssertDenied(reader, "DELETE FROM ticks WHERE 1 = 1");
        AssertDenied(reader, "TRUNCATE TABLE ticks");
        AssertDenied(reader, "OPTIMIZE TABLE ticks FINAL");
        AssertDenied(reader, "DROP TABLE ticks");

        Assert.Equal(1, Count(request.ConnectionString, "ticks"));
    }

    [Fact(DisplayName = "Grants Should keep both accounts away from the journal and from DDL")]
    public void Grants_Should_keep_both_accounts_away_from_the_journal_and_from_DDL()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var exporter = ExporterConnectionString(request);
        var reader = ReaderConnectionString(request);

        // act, assert, a forged journal entry would hide a script from every later run
        AssertDenied(exporter, "INSERT INTO schema_versions (script_name, applied_at) VALUES ('forged', now64(6))");
        AssertDenied(exporter, "SELECT count() FROM schema_versions");
        AssertDenied(reader, "SELECT count() FROM schema_versions");

        AssertDenied(exporter, "CREATE TABLE side (id UInt64) ENGINE = Memory");
        AssertDenied(reader, "CREATE TABLE side (id UInt64) ENGINE = Memory");
    }

    /// <summary>
    /// Roles are named after the database, so deployments sharing a server cannot reach each other's ticks.
    /// </summary>
    [Fact(DisplayName = "Grants Should confine the accounts of each deployment to its own database")]
    public void Grants_Should_confine_the_accounts_of_each_deployment_to_its_own_database()
    {
        // arrange, two deployments on one server
        var first = CreateRequest();
        var second = CreateRequest();
        AssertSuccessful(CreateRunner().Run(first));
        AssertSuccessful(CreateRunner().Run(second));

        // act, assert
        Insert(ExporterConnectionString(first), new Tick(1, At(0)));

        AssertDenied(ExporterConnectionString(first), InsertInto($"{second.DatabaseName}.ticks", new Tick(1, At(0))));
        AssertDenied(ReaderConnectionString(first), $"SELECT count() FROM {second.DatabaseName}.ticks");

        Assert.Equal(1, Count(first.ConnectionString, "ticks"));
        Assert.Equal(0, Count(second.ConnectionString, "ticks"));
    }

    /// <summary>
    /// ro.sql revokes the exporter role before granting its own, so the account converges on read only.
    /// </summary>
    [Fact(DisplayName = "Grants Should take INSERT away when an exporter is re-provisioned as read only")]
    public void Grants_Should_take_INSERT_away_when_an_exporter_is_re_provisioned_as_read_only()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var account = request.Users.Single(user => user.Access == "exporter");

        // act, the same account, now read only
        AssertSuccessful(CreateRunner().Run(request with { Users = [account with { Access = "ro" }] }));

        // assert
        var connectionString = WithUser(request.ConnectionString, account);

        Assert.Equal(0, Count(connectionString, "ticks"));
        AssertDenied(connectionString, InsertSql(new Tick(1, At(0))));

        // and revoked, not merely left inactive behind DEFAULT ROLE where SET ROLE could bring it back
        Assert.Equal([$"role_{request.DatabaseName}_ro"], GrantedRoles(request.ConnectionString, account.User));
    }

    /// <summary>
    /// DEFAULT ROLE decides what is active at login, whatever else the account was granted before.
    /// </summary>
    [Fact(DisplayName = "Grants Should log a reader re-provisioned as exporter in with the exporter role alone")]
    public void Grants_Should_log_a_reader_re_provisioned_as_exporter_in_with_the_exporter_role_alone()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var account = request.Users.Single(user => user.Access == "ro");

        // act, the same account, now the exporter
        AssertSuccessful(CreateRunner().Run(request with { Users = [account with { Access = "exporter" }] }));

        // assert
        var connectionString = WithUser(request.ConnectionString, account);

        Assert.Equal($"role_{request.DatabaseName}_exporter", Scalar<string>(connectionString, "SELECT arrayStringConcat(currentRoles(), ',')"));

        Insert(connectionString, new Tick(1, At(0)));
        AssertDenied(connectionString, "SELECT count() FROM ticks");
    }

    /// <summary>
    /// The password is re-applied on every run, so a rotation takes effect at once and the old one stops working.
    /// </summary>
    [Fact(DisplayName = "Grants Should switch the exporter to a rotated password")]
    public void Grants_Should_switch_the_exporter_to_a_rotated_password()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var account = request.Users.Single(user => user.Access == "exporter");
        var rotated = account with { Password = "rotated" };

        // act
        AssertSuccessful(CreateRunner().Run(request with { Users = [rotated] }));

        // assert
        Insert(WithUser(request.ConnectionString, rotated), new Tick(1, At(0)));
        AssertError(WithUser(request.ConnectionString, account), InsertSql(new Tick(2, At(1))), AuthenticationFailed);

        Assert.Equal([1L], Sequences(request.ConnectionString));
    }

    // ---------------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// One tick. Prices and volumes are SQL literals, so no value is rounded on its way to the server.
    /// </summary>
    private sealed record Tick(
        ulong Sequence,
        DateTime CreatedTime,
        string Symbol = "EURUSD",
        ulong SenderId = Sender,
        ulong SymbolId = 1,
        string BidPrice = "1.0855",
        string BidVolume = "1000000",
        string AskPrice = "1.0856",
        string AskVolume = "500000",
        DateTime? BatchTime = null)
    {
        public string ToSql()
        {
            return $"({SenderId}, {Sequence}, {Time(BatchTime ?? CreatedTime)}, {Time(CreatedTime)}, {SymbolId}, {Quote(Symbol)}, " +
                   $"{BidPrice}, {BidVolume}, {AskPrice}, {AskVolume})";
        }
    }

    private DateTime At(double seconds)
    {
        return recent.AddSeconds(seconds);
    }

    private static DateTime DaysAgo(int days)
    {
        return DateTime.UtcNow.AddDays(-days);
    }

    private static DateTime HoursAgo(int hours)
    {
        return DateTime.UtcNow.AddHours(-hours);
    }

    private static DateTime Utc(string time)
    {
        return DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    /// <summary>
    /// A cut-off on the 15th of last month, and the retention in days that puts it there.
    /// </summary>
    private static (DateTime CutOff, int Days) CutOffMidLastMonth()
    {
        var now = DateTime.UtcNow;
        var lastMonth = now.AddMonths(-1);
        var cutOff = new DateTime(lastMonth.Year, lastMonth.Month, 15, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);

        return (cutOff, (now.Date - cutOff.Date).Days);
    }

    /// <summary>
    /// A time as ClickHouse prints a DateTime64(6).
    /// </summary>
    private static string Format(DateTime time)
    {
        return time.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    private static string Time(DateTime time)
    {
        return $"'{Format(time)}'";
    }

    private static string Quote(string value)
    {
        return $"'{SqlSafety.EscapeLiteral(DatabaseProvider.ClickHouse, value)}'";
    }

    private static string Values(params Tick[] ticks)
    {
        return string.Join(", ", ticks.Select(tick => tick.ToSql()));
    }

    private static string InsertInto(string table, params Tick[] ticks)
    {
        return $"INSERT INTO {table} ({TickColumns}) VALUES {Values(ticks)}";
    }

    private static string InsertSql(params Tick[] ticks)
    {
        return InsertInto("ticks", ticks);
    }

    /// <summary>
    /// One statement, one batch, the shape the exporter sends.
    /// </summary>
    private static void Insert(string connectionString, params Tick[] ticks)
    {
        Execute(connectionString, InsertSql(ticks));
    }

    /// <summary>
    /// Runs the TTL pass now rather than on its four-hourly schedule (merge_with_ttl_timeout).
    /// </summary>
    private static void ApplyRetention(string connectionString)
    {
        Execute(connectionString, "ALTER TABLE ticks MATERIALIZE TTL SETTINGS mutations_sync = 2");
    }

    /// <summary>
    /// The ticks table as an operator would have created it by hand from the guide, on the given engine.
    /// </summary>
    private static string HandMadeTicksTable(string engine)
    {
        return $"""
                CREATE TABLE ticks
                (
                    sender_id UInt64, sequence UInt64,
                    batch_time DateTime64(6, 'UTC') CODEC(Delta, Default), created_time DateTime64(6, 'UTC') CODEC(Delta, Default),
                    symbol_id UInt64, short_symbol_name LowCardinality(String),
                    bid_price Decimal128(12), bid_volume Decimal64(8), ask_price Decimal128(12), ask_volume Decimal64(8)
                )
                ENGINE = {engine}
                PARTITION BY toYYYYMM(created_time)
                PRIMARY KEY (sender_id, short_symbol_name)
                ORDER BY (sender_id, short_symbol_name, created_time, sequence)
                """;
    }

    private static List<string> Books(string connectionString)
    {
        return Strings(connectionString, $"SELECT {BookColumns} FROM ticks ORDER BY sequence");
    }

    private static List<long> Sequences(string connectionString)
    {
        return Longs(connectionString, "SELECT sequence FROM ticks ORDER BY sequence");
    }

    private static long Count(string connectionString, string from)
    {
        return Scalar<long>(connectionString, $"SELECT count() FROM {from}");
    }

    private static List<string> Tables(string connectionString)
    {
        return Strings(connectionString, "SELECT name FROM system.tables WHERE database = currentDatabase() ORDER BY name");
    }

    private static string EngineFull(string connectionString, string table)
    {
        return Scalar<string>(connectionString, $"SELECT engine_full FROM system.tables WHERE database = currentDatabase() AND name = '{table}'");
    }

    private static List<string> GrantedRoles(string connectionString, string userName)
    {
        return Strings(connectionString, $"SELECT granted_role_name FROM system.role_grants WHERE user_name = '{userName}' ORDER BY granted_role_name");
    }

    private static List<string> RoleGrants(string connectionString, string roleName)
    {
        return Strings(connectionString, $"SELECT concat(access_type, ' ON ', table) FROM system.grants WHERE role_name = '{roleName}' ORDER BY table");
    }

    private static List<string> TicksJournalEntries(string connectionString)
    {
        return Strings(connectionString,
            "SELECT script_name FROM schema_versions WHERE script_name LIKE '%create_ticks.sql' OR script_name LIKE '%ticks_ttl.sql::%' ORDER BY script_name");
    }

    /// <summary>
    /// Runs the statement and asserts the server refused it with the given code, naming the statement if not.
    /// </summary>
    private static void AssertError(string connectionString, string sql, int errorCode)
    {
        var exception = Record.Exception(() => Execute(connectionString, sql));

        Assert.True(exception is ClickHouseServerException server && server.ErrorCode == errorCode,
            $"Expected error {errorCode} from: {sql}{Environment.NewLine}Got: {exception?.Message ?? "no error"}");
    }

    private static void AssertDenied(string connectionString, string sql)
    {
        AssertError(connectionString, sql, AccessDenied);
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand(sql);

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static List<string> Strings(string connectionString, string sql)
    {
        return Column(connectionString, sql, value => Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private static List<long> Longs(string connectionString, string sql)
    {
        return Column(connectionString, sql, value => Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static List<T> Column<T>(string connectionString, string sql, Func<object, T> convert)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand(sql);
        using var reader = command.ExecuteReader();

        var values = new List<T>();

        while (reader.Read())
        {
            values.Add(convert(reader.GetValue(0)));
        }

        return values;
    }

    private static string ExporterConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "exporter"));
    }

    private static string ReaderConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "ro"));
    }

    private static string WithUser(string connectionString, DatabaseUser user)
    {
        return new ClickHouseConnectionStringBuilder(connectionString) { Username = user.User, Password = user.Password }.ToString();
    }

    private static string WithDatabase(string connectionString, string databaseName)
    {
        return new ClickHouseConnectionStringBuilder(connectionString) { Database = databaseName }.ToString();
    }

    private static void CreateDatabase(DbMigratorRequest request)
    {
        Execute(WithDatabase(request.ConnectionString, "system"), $"CREATE DATABASE {request.DatabaseName}");
    }

    /// <summary>
    /// Provisions and migrates a database; returns its connection string.
    /// </summary>
    private string Migrate(int? ticksTtlDays = null)
    {
        var request = CreateRequest(ticksTtlDays);

        AssertSuccessful(CreateRunner().Run(request));

        return request.ConnectionString;
    }

    private DbUpRunner CreateRunner()
    {
        return new(NullLogger<DbUpRunner>.Instance, new TestDbUpLogProvider(output));
    }

    private DbMigratorRequest CreateRequest(int? ticksTtlDays = null)
    {
        var configured = configBuilder.ClickHouseConnectionString;

        Assert.False(string.IsNullOrWhiteSpace(configured), "No ClickHouse connection string configured. Start docker-compose.yml or set ClickHouse__ConnectionString.");

        var databaseName = $"ticks_integration_{Guid.NewGuid():N}"[..40];

        provisionedDatabases.Add((configured!, databaseName));

        return new(
            DatabaseProvider.ClickHouse,
            WithDatabase(configured!, databaseName),
            databaseName,
            "schema_versions",
            [new(true, "clickhouse")],
            configBuilder.RepositoryRoot,
            Variables(databaseName, ticksTtlDays),
            false,
            false,
            null,
            [
                new() { Access = "exporter", User = $"{databaseName}_exporter", Password = "passw0rd" },
                new() { Access = "ro", User = $"{databaseName}_reader", Password = "passw0rd" }
            ]);
    }

    /// <summary>
    /// The CLI's own variables, so new ones reach these tests automatically; a retention goes in as --variables.
    /// </summary>
    private static Dictionary<string, string> Variables(string databaseName, int? ticksTtlDays)
    {
        return MigrationHandler.BuildVariables(DatabaseProvider.ClickHouse, databaseName, null,
            new() { Variables = ticksTtlDays is null ? null : [$"ticks_ttl_days={ticksTtlDays}"] });
    }

    private static DbMigratorRequest WithRetention(DbMigratorRequest request, int days)
    {
        return request with { Variables = Variables(request.DatabaseName, days) };
    }

    private static void AssertSuccessful(IEnumerable<(string SubFolder, DatabaseUpgradeResult Result)> results)
    {
        foreach (var (subFolder, result) in results)
        {
            Assert.True(result.Successful, $"{subFolder} failed on '{result.ErrorScript?.Name}': {result.Error}");
        }
    }
}
