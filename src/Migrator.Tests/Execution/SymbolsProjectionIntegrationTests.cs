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

using DbUp.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;
using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Handlers;

namespace YB.TradeServer.Db.Migrator.Tests.Execution;

/// <summary>
/// The trigger that maintains the symbols table from the symbol event log.
/// </summary>
/// <remarks>
/// Mirrors <see cref="TradingAccountsProjectionIntegrationTests"/>. Rows are built from the live schema, zeroed
/// unless a test names a column, so the fixtures need no hand-kept column list. Each test gets its own database.
/// </remarks>
[Trait("Category", "Integration")]
public class SymbolsProjectionIntegrationTests : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    private const long Sender = 7;
    private const long OtherSender = 8;

    /// <summary>
    /// Boolean <c>b</c> carries bit <c>p</c> of <c>b + 1</c> in event <c>p</c>, so any two booleans differ in at
    /// least one of five events. Covers up to 31 booleans.
    /// </summary>
    private const int BooleanPatterns = 5;

    private readonly ConfigBuilder configBuilder;
    private readonly ITestOutputHelper output;
    private readonly List<(string ConnectionString, string DatabaseName)> provisionedDatabases = [];

    public SymbolsProjectionIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output)
    {
        this.configBuilder = configBuilder;
        this.output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (connectionString, databaseName) in provisionedDatabases)
        {
            try
            {
                await using var connection = new NpgsqlConnection(WithDatabase(connectionString, "postgres"));
                await connection.OpenAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = $@"DROP DATABASE IF EXISTS ""{databaseName}"" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception e)
            {
                output.WriteLine($"Unable to drop {databaseName}: {e.Message}");
            }
        }
    }

    [Fact(DisplayName = "Schema Should create every object the projection needs")]
    public void Schema_Should_create_every_object_the_projection_needs()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.True(TableExists(connectionString, "symbol_events"));
        Assert.True(TableExists(connectionString, "symbols"));
        Assert.True(TableExists(connectionString, "symbols_resync"));

        Assert.True(IndexExists(connectionString, "idx_symbol_row_symbol_events"));
        Assert.True(IndexExists(connectionString, "idx_markers_symbol_events"));

        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_project_symbols' AND NOT tgisinternal"));
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_proc WHERE proname = 'project_symbols'"));
    }

    /// <summary>
    /// A type drifting between the two tables would not fail the migration; it would truncate or round.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the same symbol columns in the log and the state table")]
    public void Schema_Should_carry_the_same_symbol_columns_in_the_log_and_the_state_table()
    {
        // arrange
        var connectionString = Migrate();

        // act, format_type so a VARCHAR width drift shows
        const string sql = """
                           WITH cols AS (SELECT c.relname                            AS table_name,
                                                a.attname                            AS column_name,
                                                format_type(a.atttypid, a.atttypmod) AS column_type
                                         FROM pg_attribute a
                                                  JOIN pg_class c ON c.oid = a.attrelid
                                                  JOIN pg_namespace n ON n.oid = c.relnamespace
                                         WHERE n.nspname = 'public'
                                           AND a.attnum > 0
                                           AND NOT a.attisdropped
                                           AND c.relname IN ('symbol_events', 'symbols'))
                           SELECT COALESCE(e.column_name, s.column_name) || ' (' ||
                                  COALESCE(e.column_type, 'absent') || ' / ' || COALESCE(s.column_type, 'absent') || ')'
                           FROM      (SELECT column_name, column_type FROM cols
                                      WHERE table_name = 'symbol_events'
                                        AND column_name NOT IN ('id', 'time_inserted', 'event_type')) e
                           FULL JOIN (SELECT column_name, column_type FROM cols
                                      WHERE table_name = 'symbols'
                                        AND column_name NOT IN ('source_event_row_id', 'time_applied')) s
                                  ON e.column_name = s.column_name
                           WHERE e.column_name IS NULL OR s.column_name IS NULL OR e.column_type <> s.column_type
                           """;

        // assert
        Assert.Empty(Strings(connectionString, sql));
    }

    /// <summary>
    /// The agreed schema, written out: every other fixture reads the live schema, so only this test fails if
    /// symbol_events is reordered or retyped.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the agreed columns and types in the agreed order")]
    public void Schema_Should_carry_the_agreed_columns_and_types_in_the_agreed_order()
    {
        // arrange
        var connectionString = Migrate();

        string[] agreed =
        [
            "id bigint", "time_inserted timestamp with time zone", "sender_id bigint", "event_type smallint",
            "symbol_id bigint", "throttling_price_update_count integer", "spread_balance integer",
            "decimal_places integer", "allow_negative_prices boolean", "allow_realtime_quotes boolean",
            "market_depth integer", "min_spread_filter integer", "max_spread_filter integer", "soft_filter integer",
            "soft_filter_tick_count integer", "hard_filter integer", "hard_filter_tick_count integer",
            "discard_filter integer", "gap_diff integer", "gap_tick_count integer", "max_price_limit numeric",
            "min_price_limit numeric", "tick_value numeric", "tick_size numeric", "lot_size numeric",
            "base_currency_decimal_places integer", "profit_currency_decimal_places integer",
            "margin_currency_decimal_places integer", "trade_mode integer", "margin_calc_mode integer",
            "profit_calc_mode integer", "min_order_size_lots numeric", "max_order_size_lots numeric",
            "order_size_increment_lots numeric", "max_position_lots numeric", "stops_level integer",
            "freeze_level integer", "allow_market_orders boolean", "allow_limit_orders boolean",
            "allow_stop_orders boolean", "allow_stop_limit_orders boolean", "allow_stop_loss_orders boolean",
            "allow_take_profit_orders boolean", "allow_close_by_orders boolean", "chart_mode integer",
            "quotes_timeout_seconds integer", "initial_margin numeric", "maintenance_margin numeric",
            "hedged_margin numeric", "initial_buy_margin_rate numeric", "initial_sell_margin_rate numeric",
            "initial_buy_limit_margin_rate numeric", "initial_sell_limit_margin_rate numeric",
            "initial_buy_stop_margin_rate numeric", "initial_sell_stop_margin_rate numeric",
            "initial_buy_stop_limit_margin_rate numeric", "initial_sell_stop_limit_margin_rate numeric",
            "maintenance_buy_margin_rate numeric", "maintenance_sell_margin_rate numeric",
            "maintenance_buy_limit_margin_rate numeric", "maintenance_sell_limit_margin_rate numeric",
            "maintenance_buy_stop_margin_rate numeric", "maintenance_sell_stop_margin_rate numeric",
            "maintenance_buy_stop_limit_margin_rate numeric", "maintenance_sell_stop_limit_margin_rate numeric",
            "percentage numeric", "symbol_name character varying(32)", "base_currency character varying(16)",
            "profit_currency character varying(16)", "margin_currency character varying(16)",
            "margin_use_larger_leg boolean", "recalculate_margin_rate_at_eod boolean",
            "margin_check_on_sl_tp boolean", "allow_fok boolean", "allow_ioc boolean", "allow_gtc boolean",
            "allow_gtd boolean", "allow_day_orders boolean", "allow_valid_for_milliseconds boolean",
            "swap_long numeric", "swap_short numeric", "swap_rate_sunday numeric", "swap_rate_monday numeric",
            "swap_rate_tuesday numeric", "swap_rate_wednesday numeric", "swap_rate_thursday numeric",
            "swap_rate_friday numeric", "swap_rate_saturday numeric", "swap_year_days integer", "swap_mode integer",
            "version bigint", "start_date_time timestamp with time zone", "end_date_time timestamp with time zone",
            "symbol_path character varying(128)", "description character varying(64)",
            "exchange character varying(64)", "country character varying(64)", "webpage character varying(256)",
            "underlying_asset character varying(32)", "isin character varying(16)", "cfi character varying(8)",
            "liquidity_margin_rate numeric", "bid_markup integer", "ask_markup integer", "swap_interval smallint",
            "eligible_for_currency_conversion boolean", "swap_intraday_starting_time integer",
            "price_corridor_threshold numeric", "industry_tag character varying(128)",
            "delay_interval_minutes integer", "delay_mode integer"
        ];

        // act
        var actual = Strings(connectionString,
            """
            SELECT a.attname || ' ' || format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            WHERE a.attrelid = 'symbol_events'::regclass
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """);

        // assert
        Assert.Equal(agreed, actual);
    }

    [Fact(DisplayName = "Edge case Should let the last event of a batch win for one symbol")]
    public void Edge_case_Should_let_the_last_event_of_a_batch_win_for_one_symbol()
    {
        // arrange
        var connectionString = Migrate();

        // act, one statement carrying a creation, a modification and an unrelated symbol
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, decimalPlaces: 3, description: "v1"),
            StateEvent(connectionString, Sender, 26, version: 1, decimalPlaces: 5, description: "other"),
            StateEvent(connectionString, Sender, 25, version: 2, decimalPlaces: 2, description: "v2"));

        // assert
        Assert.Equal([(25L, 2L, 2, "v2"), (26L, 1L, 5, "other")], ReadSymbols(connectionString));
    }

    [Fact(DisplayName = "Edge case Should never show a symbol created and deleted in one batch")]
    public void Edge_case_Should_never_show_a_symbol_created_and_deleted_in_one_batch()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            DeleteEvent(connectionString, Sender, 25));

        // assert
        Assert.Empty(ReadSymbols(connectionString));
    }

    [Fact(DisplayName = "Edge case Should ignore a deletion for a symbol with no row")]
    public void Edge_case_Should_ignore_a_deletion_for_a_symbol_with_no_row()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));

        // act
        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 9999));

        // assert
        Assert.Single(ReadSymbols(connectionString));
    }

    /// <summary>
    /// Why the sweep runs at the end marker: a resync spans several transactions.
    /// </summary>
    [Fact(DisplayName = "Workflow Should keep the table complete while a resync is running")]
    public void Workflow_Should_keep_the_table_complete_while_a_resync_is_running()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, Sender, 26, version: 1));

        // act, the start marker arrives in a transaction of its own
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));

        // assert
        Assert.Equal(2, ReadSymbols(connectionString).Count);
        Assert.True(ResyncInProgress(connectionString, Sender));
    }

    [Fact(DisplayName = "Workflow Should converge the table on the live set a resync listed")]
    public void Workflow_Should_converge_the_table_on_the_live_set_a_resync_listed()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, Sender, 26, version: 1));

        // act, 26 was deleted while the sender was down, so the resync re-announces only 25
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Equal([25L], ReadSymbols(connectionString).Select(symbol => symbol.SymbolId));
        Assert.False(ResyncInProgress(connectionString, Sender));
    }

    [Fact(DisplayName = "Edge case Should remove nothing for an end marker with no start marker")]
    public void Edge_case_Should_remove_nothing_for_an_end_marker_with_no_start_marker()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));

        // act
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Single(ReadSymbols(connectionString));
    }

    /// <summary>
    /// An AFTER ROW trigger sees the whole batch, so an unbounded max(id) read the next resync's start marker
    /// and swept everything the finishing one re-announced.
    /// </summary>
    [Fact(DisplayName = "Regression Should sweep against its own start marker when a batch carries two resyncs")]
    public void Regression_Should_sweep_against_its_own_start_marker_when_a_batch_carries_two_resyncs()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 100, version: 1),
            StateEvent(connectionString, Sender, 200, version: 1));

        // act, one statement ending the first resync and opening a second
        InsertOneStatement(connectionString,
            Marker(connectionString, Sender, eventType: 3),
            Marker(connectionString, Sender, eventType: 2));

        // assert, both symbols were re-announced under the resync that just finished, so both survive
        Assert.Equal([100L, 200L], ReadSymbols(connectionString).Select(symbol => symbol.SymbolId));
    }

    [Fact(DisplayName = "Edge case Should store an unrecognised event type without projecting it")]
    public void Edge_case_Should_store_an_unrecognised_event_type_without_projecting_it()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1, decimalPlaces: 3));

        // act, raising here would abort the whole exporting COPY
        InsertOneStatement(connectionString,
            Event(connectionString, Sender, eventType: 99, ("symbol_id", "25"), ("version", "7"), ("description", "'unknown'")));

        // assert
        Assert.Equal(1, Scalar<long>(connectionString, "SELECT count(*) FROM symbol_events WHERE event_type = 99"));
        Assert.Equal([(25L, 1L, 3, "")], ReadSymbols(connectionString));
    }

    /// <summary>
    /// No dedup key on the log, so the state table has to absorb a redelivery.
    /// </summary>
    [Fact(DisplayName = "Edge case Should leave the same state when an event is redelivered")]
    public void Edge_case_Should_leave_the_same_state_when_an_event_is_redelivered()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 3, decimalPlaces: 3, description: "v3"));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 3, decimalPlaces: 3, description: "v3"));

        // assert
        Assert.Equal(2, Scalar<long>(connectionString, "SELECT count(*) FROM symbol_events"));
        Assert.Equal([(25L, 3L, 3, "v3")], ReadSymbols(connectionString));
    }

    /// <summary>
    /// A reused id restarts its version, so a version guard would keep showing the old instrument.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new symbol when an id is reused after deletion")]
    public void Edge_case_Should_show_the_new_symbol_when_an_id_is_reused_after_deletion()
    {
        // arrange
        var connectionString = Migrate();

        // act, the old symbol reaches version 3, is deleted, and the id is taken by a different instrument
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 800, version: 1, name: "EURUSD", decimalPlaces: 5, description: "old"),
            StateEvent(connectionString, Sender, 800, version: 3, name: "EURUSD", decimalPlaces: 5, description: "old"),
            DeleteEvent(connectionString, Sender, 800),
            StateEvent(connectionString, Sender, 800, version: 1, name: "XAUUSD", decimalPlaces: 2, description: "new"));

        // assert
        Assert.Equal([(800L, 1L, 2, "new")], ReadSymbols(connectionString));
        Assert.Equal("XAUUSD", ReadSymbolName(connectionString, 800));
    }

    /// <summary>
    /// The same, with the deletion missed while the sender was down.
    /// </summary>
    [Fact(DisplayName = "Edge case Should show the new symbol when a reused id is first seen in a resync")]
    public void Edge_case_Should_show_the_new_symbol_when_a_reused_id_is_first_seen_in_a_resync()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 900, version: 4, name: "EURUSD", decimalPlaces: 5, description: "old"));

        // act
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 900, version: 1, name: "XAUUSD", decimalPlaces: 2, description: "new"));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert
        Assert.Equal([(900L, 1L, 2, "new")], ReadSymbols(connectionString));
        Assert.Equal("XAUUSD", ReadSymbolName(connectionString, 900));
    }

    /// <summary>
    /// Why symbol_name is not unique: a unique index would fail this re-announcement and stall the export.
    /// </summary>
    [Fact(DisplayName = "Edge case Should hold two symbols under one name while a resync is converging")]
    public void Edge_case_Should_hold_two_symbols_under_one_name_while_a_resync_is_converging()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 5, version: 3, name: "EURUSD", description: "old"));

        // act, 5 was deleted while the sender was down and EURUSD re-created as 9
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 9, version: 1, name: "EURUSD", description: "new"));

        // assert, both hold the name mid-resync
        const string byName = "SELECT symbol_id::text FROM symbols WHERE symbol_name = 'EURUSD' ORDER BY symbol_id";

        Assert.Equal(["5", "9"], Strings(connectionString, byName));
        Assert.True(ResyncInProgress(connectionString, Sender));

        // and the end marker sweeps the old one
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        Assert.Equal(["9"], Strings(connectionString, byName));
    }

    /// <summary>
    /// A float anywhere on the way would round the swap below.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep prices, sizes and swaps as exact decimals")]
    public void Edge_case_Should_keep_prices_sizes_and_swaps_as_exact_decimals()
    {
        // arrange
        var connectionString = Migrate();

        // act, the API's USDJPY sample, plus a swap with more digits than a double holds
        InsertOneStatement(connectionString,
            Event(connectionString, Sender, eventType: 0,
                ("symbol_id", "25"),
                ("version", "3"),
                ("symbol_name", "'USDJPY'"),
                ("tick_size", "0.001"),
                ("lot_size", "100000"),
                ("min_order_size_lots", "0.01"),
                ("hedged_margin", "0.5"),
                ("swap_long", "-7.123456789012345678")));

        // assert
        Assert.Equal(["0.001", "100000", "0.01", "0.5", "-7.123456789012345678"], Strings(connectionString,
            "SELECT unnest(ARRAY[tick_size, lot_size, min_order_size_lots, hedged_margin, swap_long])::text " +
            "FROM symbols WHERE symbol_id = 25"));
    }

    [Fact(DisplayName = "Edge case Should keep two senders in one database independent")]
    public void Edge_case_Should_keep_two_senders_in_one_database_independent()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, OtherSender, 5001, version: 1));

        // act, a complete resync for one sender that re-announces nothing at all
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert, the other sender's symbol is untouched
        Assert.Equal([5001L], ReadSymbols(connectionString).Select(symbol => symbol.SymbolId));
    }

    /// <summary>
    /// Unlike the test above, this one fails if any write path is keyed on symbol_id alone.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep the same symbol id independent across senders")]
    public void Edge_case_Should_keep_the_same_symbol_id_independent_across_senders()
    {
        // arrange, one symbol id, two senders, different content
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, decimalPlaces: 3, description: "sender 7"),
            StateEvent(connectionString, OtherSender, 25, version: 9, decimalPlaces: 2, description: "sender 8"));

        Assert.Equal(2, ReadSymbols(connectionString).Count);
        Assert.Equal("sender 7", ReadDescription(connectionString, Sender, 25));
        Assert.Equal("sender 8", ReadDescription(connectionString, OtherSender, 25));

        // act, one sender modifies the id, then deletes it, then resyncs re-announcing nothing
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 2, decimalPlaces: 4, description: "sender 7 v2"));
        Assert.Equal("sender 8", ReadDescription(connectionString, OtherSender, 25));

        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 25));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // assert, the other sender's symbol of the same id survives all of it, unchanged
        Assert.Equal(0, Scalar<long>(connectionString, $"SELECT count(*) FROM symbols WHERE sender_id = {Sender}"));
        Assert.Equal([(25L, 9L, 2, "sender 8")], ReadSymbols(connectionString));
    }

    /// <summary>
    /// Each sender numbers its own symbols. The wrong join is asserted too, so the fixture keeps reproducing it.
    /// </summary>
    [Fact(DisplayName = "Invariant Should need sender_id as well as symbol_id to join trades to their symbol")]
    public void Invariant_Should_need_sender_id_as_well_as_symbol_id_to_join_trades_to_their_symbol()
    {
        // arrange, one symbol id used by two senders for two different instruments, with trades on each
        var connectionString = Migrate();

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, name: "USDJPY"),
            StateEvent(connectionString, OtherSender, 25, version: 1, name: "XAUUSD"));

        Execute(connectionString,
            $"INSERT INTO trades ({Columns(connectionString, "trades")}) VALUES " +
            string.Join(", ",
                Trade(connectionString, Sender, tradeId: 1, symbolId: 25, symbolName: "USDJPY"),
                Trade(connectionString, Sender, tradeId: 2, symbolId: 25, symbolName: "USDJPY"),
                Trade(connectionString, OtherSender, tradeId: 1, symbolId: 25, symbolName: "XAUUSD")));

        // act, the full key against the id alone
        const string fullKey = "FROM trades t JOIN symbols s ON s.sender_id = t.sender_id AND s.symbol_id = t.symbol_id";
        const string idOnly = "FROM trades t JOIN symbols s ON s.symbol_id = t.symbol_id";

        // assert, three trades have a symbol, and joining on the id alone invents three more
        Assert.Equal(3, Scalar<long>(connectionString, $"SELECT count(*) {fullKey}"));
        Assert.Equal(6, Scalar<long>(connectionString, $"SELECT count(*) {idOnly}"));

        // and only the full key pairs each trade with its own instrument
        Assert.Equal(0, Scalar<long>(connectionString, $"SELECT count(*) {fullKey} WHERE s.symbol_name <> t.symbol_name"));
        Assert.Equal(3, Scalar<long>(connectionString, $"SELECT count(*) {idOnly} WHERE s.symbol_name <> t.symbol_name"));
    }

    /// <summary>
    /// A sweep leaves no event, so a naive reconcile would replay the swept symbol's last state.
    /// </summary>
    [Fact(DisplayName = "Regression Should not bring back a swept symbol when the projection runs again")]
    public void Regression_Should_not_bring_back_a_swept_symbol_when_the_projection_runs_again()
    {
        // arrange, 26 is swept by a completed resync, leaving 25 live
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1),
            StateEvent(connectionString, Sender, 26, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        // and then the trigger is dropped by hand while the export carries on
        Execute(connectionString, "DROP TRIGGER trg_project_symbols ON symbol_events");
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 6001, version: 1, decimalPlaces: 4, description: "while broken"));
        InsertOneStatement(connectionString, DeleteEvent(connectionString, Sender, 25));

        Assert.Equal([25L], ReadSymbols(connectionString).Select(symbol => symbol.SymbolId));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the drift is healed, the trigger is back, and the swept symbol stays swept
        Assert.Equal([(6001L, 1L, 4, "while broken")], ReadSymbols(connectionString));
        Assert.Equal(1, Scalar<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'trg_project_symbols' AND NOT tgisinternal"));
    }

    /// <summary>
    /// The sweep lives in the trigger, so the reconcile has to apply one the trigger missed.
    /// </summary>
    [Fact(DisplayName = "Regression Should apply a resync sweep that happened while the trigger was absent")]
    public void Regression_Should_apply_a_resync_sweep_that_happened_while_the_trigger_was_absent()
    {
        // arrange, two live symbols, both projected
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 101, version: 1, description: "re-announced"),
            StateEvent(connectionString, Sender, 102, version: 1, description: "dropped by the resync"));

        Assert.Equal(2, ReadSymbols(connectionString).Count);

        // act, the trigger is gone for the whole resync, which re-announces 101 only
        Execute(connectionString, "DROP TRIGGER trg_project_symbols ON symbol_events");

        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 101, version: 1, description: "re-announced"));
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 3));

        Assert.Equal(2, ReadSymbols(connectionString).Count);

        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Equal([(101L, 1L, 0, "re-announced")], ReadSymbols(connectionString));
    }

    /// <summary>
    /// The sender dies mid-resync, so no end marker arrives.
    /// </summary>
    [Fact(DisplayName = "Edge case Should leave the table converging when a resync never finishes")]
    public void Edge_case_Should_leave_the_table_converging_when_a_resync_never_finishes()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, description: "before"),
            StateEvent(connectionString, Sender, 26, version: 1, description: "not reached"));

        // act, the resync starts and re-announces one symbol, then the sender dies
        InsertOneStatement(connectionString, Marker(connectionString, Sender, eventType: 2));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 2, description: "re-announced"));

        // assert, the unreached symbol keeps its old state and the sender stays flagged
        Assert.Equal([(25L, 2L, 0, "re-announced"), (26L, 1L, 0, "not reached")], ReadSymbols(connectionString));
        Assert.True(ResyncInProgress(connectionString, Sender));
    }

    /// <summary>
    /// Guards the projection's four column lists: drives each in turn and compares event and state rows as
    /// jsonb. Every value is distinct per column and changes on every write, so a crossed or forgotten column shows.
    /// </summary>
    [Fact(DisplayName = "Invariant Should copy every field from the event unchanged on all four write paths")]
    public void Invariant_Should_copy_every_field_from_the_event_unchanged_on_all_four_write_paths()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        Assert.True(EventSchema(connectionString).Count(column => column.Type == "boolean") < 1 << BooleanPatterns,
            $"More booleans than {BooleanPatterns} patterns can tell apart; raise BooleanPatterns.");

        // act, the trigger's INSERT
        InsertOneStatement(connectionString, DistinctiveStateEvents(connectionString, firstSymbolId: 101, seed: 1, flip: false));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // act, the trigger's SET
        InsertOneStatement(connectionString, DistinctiveStateEvents(connectionString, firstSymbolId: 101, seed: 2, flip: true));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // act, the reconcile's SET (101-105) and INSERT (201-205)
        Execute(connectionString, "DROP TRIGGER trg_project_symbols ON symbol_events");

        InsertOneStatement(connectionString, DistinctiveStateEvents(connectionString, firstSymbolId: 101, seed: 3, flip: false));
        InsertOneStatement(connectionString, DistinctiveStateEvents(connectionString, firstSymbolId: 201, seed: 3, flip: false));

        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Empty(Mismatches(connectionString));

        // and the rows compared were populated by the last write
        Assert.Equal(2 * BooleanPatterns, Scalar<long>(connectionString,
            "SELECT count(*) FROM symbols WHERE description LIKE '3-%' AND lot_size <> 0"));
    }

    [Fact(DisplayName = "Invariant Should name the log event that produced every state row")]
    public void Invariant_Should_name_the_log_event_that_produced_every_state_row()
    {
        // arrange
        var connectionString = Migrate();

        // act
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 1));
        InsertOneStatement(connectionString, StateEvent(connectionString, Sender, 25, version: 2));

        // assert, stamped with the newest state event
        const string notNewest = """
                                 SELECT s.symbol_id::text
                                 FROM symbols s
                                          JOIN LATERAL (SELECT id
                                                        FROM symbol_events e
                                                        WHERE e.sender_id = s.sender_id
                                                          AND e.symbol_id = s.symbol_id
                                                          AND e.event_type = 0
                                                        ORDER BY e.id DESC
                                                        LIMIT 1) newest ON true
                                 WHERE s.source_event_row_id <> newest.id
                                 """;

        Assert.Empty(Strings(connectionString, notNewest));
    }

    /// <summary>
    /// Why the state is a table, not a view: it has to outlive the log's retention.
    /// </summary>
    [Fact(DisplayName = "Invariant Should keep the current state when the log is purged")]
    public void Invariant_Should_keep_the_current_state_when_the_log_is_purged()
    {
        // arrange
        var connectionString = Migrate();
        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 2, decimalPlaces: 3, description: "survives"),
            StateEvent(connectionString, Sender, 26, version: 1, decimalPlaces: 5, description: "also survives"));

        // act, a purge is a DELETE; the trigger fires on INSERT only
        Execute(connectionString, "DELETE FROM symbol_events");

        // assert
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM symbol_events"));
        Assert.Equal([(25L, 2L, 3, "survives"), (26L, 1L, 5, "also survives")], ReadSymbols(connectionString));
    }

    [Fact(DisplayName = "Invariant Should key the state table on sender and symbol")]
    public void Invariant_Should_key_the_state_table_on_sender_and_symbol()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        const string primaryKey = """
                                  SELECT string_agg(a.attname, ', ' ORDER BY k.ord)
                                  FROM pg_index i
                                           JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
                                           JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                                  WHERE i.indrelid = 'symbols'::regclass
                                    AND i.indisprimary
                                  """;

        Assert.Equal("sender_id, symbol_id", Scalar<string>(connectionString, primaryKey));
    }

    /// <summary>
    /// No lag: the state row is written in the event's transaction.
    /// </summary>
    [Fact(DisplayName = "Invariant Should project inside the writing transaction and roll back with it")]
    public void Invariant_Should_project_inside_the_writing_transaction_and_roll_back_with_it()
    {
        // arrange
        var connectionString = Migrate();
        var columns = EventColumns(connectionString);
        var row = StateEvent(connectionString, Sender, 25, version: 1);

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        // act
        using (var transaction = connection.BeginTransaction())
        {
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO symbol_events ({columns}) VALUES {row}";
                insert.ExecuteNonQuery();
            }

            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT count(*) FROM symbols";

            // assert, visible before commit
            Assert.Equal(1L, (long)read.ExecuteScalar()!);

            transaction.Rollback();
        }

        // and gone after rollback
        Assert.Empty(ReadSymbols(connectionString));
        Assert.Equal(0, Scalar<long>(connectionString, "SELECT count(*) FROM symbol_events"));
    }

    /// <summary>
    /// A correctly stamped, wrong row, as a buggy projection would leave; re-applying the file must fix it.
    /// </summary>
    [Fact(DisplayName = "Regression Should correct a state row whose content disagrees with its event")]
    public void Regression_Should_correct_a_state_row_whose_content_disagrees_with_its_event()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        var connectionString = request.ConnectionString;

        InsertOneStatement(connectionString,
            StateEvent(connectionString, Sender, 25, version: 1, decimalPlaces: 3, description: "correct"));

        // right stamp, wrong content
        Execute(connectionString, "UPDATE symbols SET decimal_places = 999, description = 'corrupted'");
        Assert.Equal([(25L, 1L, 999, "corrupted")], ReadSymbols(connectionString));

        // act
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert, the log wins
        Assert.Equal([(25L, 1L, 3, "correct")], ReadSymbols(connectionString));
    }

    /// <summary>
    /// The production path: the exporter role over <c>COPY</c>, including a sweep that needs DELETE.
    /// </summary>
    [Fact(DisplayName = "Grants Should let the exporter role drive the whole projection over COPY")]
    public void Grants_Should_let_the_exporter_role_drive_the_whole_projection_over_COPY()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var columns = EventColumns(request.ConnectionString);
        var schema = EventSchema(request.ConnectionString);

        using var exporter = new NpgsqlConnection(ExporterConnectionString(request));
        exporter.Open();

        // act
        Copy(exporter, columns,
            CopyLine(schema, Sender, eventType: 0, symbolId: 25, version: 1, decimalPlaces: 3, description: "live"),
            CopyLine(schema, Sender, eventType: 0, symbolId: 26, version: 1, decimalPlaces: 5, description: "swept"));

        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 2));

        // assert
        Assert.Equal([25L, 26L], ReadSymbols(request.ConnectionString).Select(symbol => symbol.SymbolId));

        // the sweep needs DELETE as the exporter
        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 0, symbolId: 25, version: 1, decimalPlaces: 3, description: "live"));
        Copy(exporter, columns, CopyLine(schema, Sender, eventType: 3));

        Assert.Equal([(25L, 1L, 3, "live")], ReadSymbols(request.ConnectionString));

        // and ro reads but cannot write
        using var reader = new NpgsqlConnection(ReaderConnectionString(request));
        reader.Open();

        using (var read = reader.CreateCommand())
        {
            read.CommandText = "SELECT count(*) FROM symbols";
            Assert.Equal(1L, (long)read.ExecuteScalar()!);
        }

        using (var read = reader.CreateCommand())
        {
            read.CommandText = $"SELECT resync_in_progress FROM symbols_resync WHERE sender_id = {Sender}";
            Assert.False((bool)read.ExecuteScalar()!);
        }

        using var write = reader.CreateCommand();
        write.CommandText = "DELETE FROM symbols";
        var denied = Assert.Throws<PostgresException>(() => write.ExecuteNonQuery());
        Assert.Equal("42501", denied.SqlState);
    }

    // ---------------------------------------------------------------------------------------------------
    // Fixtures: rows are built from the live schema, zeroed, with the named columns overridden.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A table's columns in ordinal order, minus id and time_inserted.
    /// </summary>
    private static List<(string Column, string Type)> Schema(string connectionString, string table)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT column_name, data_type
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = @table
                                AND column_name NOT IN ('id', 'time_inserted')
                              ORDER BY ordinal_position
                              """;
        command.Parameters.AddWithValue("table", table);

        using var reader = command.ExecuteReader();

        var columns = new List<(string, string)>();

        while (reader.Read())
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        return columns;
    }

    private static List<(string Column, string Type)> EventSchema(string connectionString)
    {
        return Schema(connectionString, "symbol_events");
    }

    private static string Columns(string connectionString, string table)
    {
        return string.Join(", ", Schema(connectionString, table).Select(column => column.Column));
    }

    private static string EventColumns(string connectionString)
    {
        return Columns(connectionString, "symbol_events");
    }

    /// <summary>
    /// The exporter's zero for an unused field, as a SQL literal.
    /// </summary>
    private static string Zero(string dataType)
    {
        return dataType switch
        {
            "character varying" => "''",
            "timestamp with time zone" => "to_timestamp(0)",
            "boolean" => "false",
            _ => "0"
        };
    }

    /// <summary>
    /// One row as a SQL tuple, zeroed, with the named columns overridden by SQL literals.
    /// </summary>
    private static string Row(string connectionString, string table, params (string Column, string Value)[] overrides)
    {
        var values = Schema(connectionString, table).Select(column =>
            overrides.FirstOrDefault(o => o.Column == column.Column).Value ?? Zero(column.Type));

        return $"({string.Join(", ", values)})";
    }

    private static string Event(string connectionString, long sender, int eventType, params (string Column, string Value)[] overrides)
    {
        return Row(connectionString, "symbol_events",
            [("sender_id", sender.ToString()), ("event_type", eventType.ToString()), .. overrides]);
    }

    /// <summary>
    /// A state event; the named fields stand in for the whole record.
    /// </summary>
    private static string StateEvent(string connectionString, long sender, long symbolId, long version,
        string name = "", int decimalPlaces = 0, string description = "")
    {
        return Event(connectionString, sender, eventType: 0,
            ("symbol_id", symbolId.ToString()),
            ("version", version.ToString()),
            ("symbol_name", Quote(name)),
            ("decimal_places", decimalPlaces.ToString()),
            ("description", Quote(description)));
    }

    private static string DeleteEvent(string connectionString, long sender, long symbolId)
    {
        return Event(connectionString, sender, eventType: 1, ("symbol_id", symbolId.ToString()));
    }

    private static string Marker(string connectionString, long sender, int eventType)
    {
        return Event(connectionString, sender, eventType);
    }

    /// <summary>
    /// One fully populated state event per boolean pattern. A new <paramref name="seed"/> changes every
    /// non-boolean value; <paramref name="flip"/> inverts every boolean.
    /// </summary>
    private static string[] DistinctiveStateEvents(string connectionString, long firstSymbolId, int seed, bool flip)
    {
        var schema = EventSchema(connectionString);

        var booleans = schema
            .Where(column => column.Type == "boolean")
            .Select((column, index) => (column.Column, Bits: index + 1))
            .ToDictionary(boolean => boolean.Column, boolean => boolean.Bits);

        return Enumerable.Range(0, BooleanPatterns).Select(pattern =>
        {
            var overrides = schema
                .Where(column => column.Column is not ("sender_id" or "event_type" or "symbol_id"))
                .Select((column, ordinal) => (column.Column, Value: column.Type switch
                {
                    // fits VARCHAR(8)
                    "character varying" => $"'{seed}-{ordinal}''s'",
                    "timestamp with time zone" => $"to_timestamp({1700000000 + seed * 1000 + ordinal})",
                    // more digits than a double holds
                    "numeric" => $"{seed * 1000 + ordinal}.000000000000000001",
                    "boolean" => (((booleans[column.Column] >> pattern) & 1) == 1) != flip ? "true" : "false",
                    // fits SMALLINT
                    _ => (seed * 1000 + ordinal + 1).ToString()
                }))
                .ToArray();

            return Event(connectionString, Sender, eventType: 0,
                [("symbol_id", (firstSymbolId + pattern).ToString()), .. overrides]);
        }).ToArray();
    }

    /// <summary>
    /// Columns where a state row differs from its source event, as <c>symbol_id: column</c>.
    /// </summary>
    private static List<string> Mismatches(string connectionString)
    {
        return Strings(connectionString,
            """
            SELECT s.symbol_id || ': ' || key
            FROM symbols s
                     JOIN symbol_events e ON e.id = s.source_event_row_id
                     JOIN LATERAL jsonb_each(to_jsonb(e) - 'id' - 'time_inserted' - 'event_type') ON true
            WHERE value IS DISTINCT FROM ((to_jsonb(s) - 'source_event_row_id' - 'time_applied') -> key)
            ORDER BY s.symbol_id, key
            """);
    }

    private static string Trade(string connectionString, long sender, long tradeId, long symbolId, string symbolName)
    {
        return Row(connectionString, "trades",
            ("sender_id", sender.ToString()),
            ("trade_id", tradeId.ToString()),
            ("symbol_id", symbolId.ToString()),
            ("symbol_name", Quote(symbolName)));
    }

    private static string Quote(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }

    /// <summary>
    /// One event as a <c>COPY</c> text line. Empty text is '', never <c>\N</c>: every column is NOT NULL.
    /// </summary>
    private static string CopyLine(List<(string Column, string Type)> schema, long sender, int eventType,
        long symbolId = 0, long version = 0, int decimalPlaces = 0, string description = "")
    {
        const string epoch = "1970-01-01 00:00:00+00";

        return string.Join('\t', schema.Select(column => column.Column switch
        {
            "sender_id" => sender.ToString(),
            "event_type" => eventType.ToString(),
            "symbol_id" => symbolId.ToString(),
            "version" => version.ToString(),
            "decimal_places" => decimalPlaces.ToString(),
            "description" => description,
            _ => column.Type switch
            {
                "character varying" => "",
                "timestamp with time zone" => epoch,
                "boolean" => "f",
                _ => "0"
            }
        }));
    }

    /// <summary>
    /// Sends rows the way the export does, over <c>COPY … FROM STDIN</c>.
    /// </summary>
    private static void Copy(NpgsqlConnection connection, string columns, params string[] rows)
    {
        using var writer = connection.BeginTextImport($"COPY symbol_events ({columns}) FROM STDIN");

        foreach (var row in rows)
        {
            writer.Write(row);
            writer.Write('\n');
        }
    }

    /// <summary>
    /// One statement and one transaction, the shape of an export batch.
    /// </summary>
    private static void InsertOneStatement(string connectionString, params string[] rows)
    {
        Execute(connectionString,
            $"INSERT INTO symbol_events ({EventColumns(connectionString)}) VALUES {string.Join(", ", rows)}");
    }

    private string ExporterConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "exporter"));
    }

    private string ReaderConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "ro"));
    }

    private static string WithUser(string connectionString, DatabaseUser user)
    {
        return new NpgsqlConnectionStringBuilder(connectionString) { Username = user.User, Password = user.Password }.ConnectionString;
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<(long SymbolId, long Version, int DecimalPlaces, string Description)> ReadSymbols(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT symbol_id, version, decimal_places, description FROM symbols ORDER BY symbol_id, sender_id";

        using var reader = command.ExecuteReader();

        var symbols = new List<(long, long, int, string)>();

        while (reader.Read())
        {
            symbols.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return symbols;
    }

    private static string ReadSymbolName(string connectionString, long symbolId)
    {
        return Scalar<string>(connectionString, $"SELECT symbol_name FROM symbols WHERE symbol_id = {symbolId}");
    }

    private static string ReadDescription(string connectionString, long sender, long symbolId)
    {
        return Scalar<string>(connectionString,
            $"SELECT description FROM symbols WHERE sender_id = {sender} AND symbol_id = {symbolId}");
    }

    private static bool ResyncInProgress(string connectionString, long sender)
    {
        return Scalar<bool>(connectionString, $"SELECT resync_in_progress FROM symbols_resync WHERE sender_id = {sender}");
    }

    private static bool TableExists(string connectionString, string tableName)
    {
        return Scalar<long>(connectionString,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{tableName}'") == 1;
    }

    private static bool IndexExists(string connectionString, string indexName)
    {
        return Scalar<long>(connectionString,
            $"SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = '{indexName}'") == 1;
    }

    private static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static List<string> Strings(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        var values = new List<string>();

        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// Provisions and migrates a database; returns its connection string.
    /// </summary>
    private string Migrate()
    {
        var request = CreateRequest();

        AssertSuccessful(CreateRunner().Run(request));

        return request.ConnectionString;
    }

    private DbUpRunner CreateRunner()
    {
        return new(NullLogger<DbUpRunner>.Instance, new TestDbUpLogProvider(output));
    }

    private DbMigratorRequest CreateRequest()
    {
        var configured = configBuilder.PostgresConnectionString;

        Assert.False(string.IsNullOrWhiteSpace(configured), "No PostgreSQL connection string configured. Start docker-compose.yml or set Postgres__ConnectionString.");

        var databaseName = $"symbol_projection_{Guid.NewGuid():N}"[..40];

        provisionedDatabases.Add((configured!, databaseName));

        return new(
            DatabaseProvider.Postgres,
            WithDatabase(configured!, databaseName),
            databaseName,
            "schema_versions",
            [new(true, "psql")],
            configBuilder.RepositoryRoot,
            // the CLI's own variables, so new ones reach these tests automatically
            MigrationHandler.BuildVariables(DatabaseProvider.Postgres, databaseName, null, new()),
            false,
            false,
            null,
            [
                new() { Access = "exporter", User = $"{databaseName}_exporter", Password = "passw0rd" },
                new() { Access = "ro", User = $"{databaseName}_reader", Password = "passw0rd" }
            ]);
    }

    private static string WithDatabase(string connectionString, string databaseName)
    {
        return new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
    }

    private static void AssertSuccessful(IEnumerable<(string SubFolder, DatabaseUpgradeResult Result)> results)
    {
        foreach (var (subFolder, result) in results)
        {
            Assert.True(result.Successful, $"{subFolder} failed on '{result.ErrorScript?.Name}': {result.Error}");
        }
    }
}
