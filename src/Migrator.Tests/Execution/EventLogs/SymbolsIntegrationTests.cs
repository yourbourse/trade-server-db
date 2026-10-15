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
/// What is particular to the symbol tables: the agreed columns, exact numerics, the one SMALLINT enum, a name two symbols
/// can share, and trades read beside their symbol.
/// </summary>
/// <remarks>
/// What the four logs share runs against this one in the EventLog suites.
/// </remarks>
[Trait("Category", "Integration")]
public class SymbolsIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    /// <summary>
    /// The agreed schema written out: the only test that fails if symbol_events is reordered or retyped.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the agreed columns and types in the agreed order")]
    public void Schema_Should_carry_the_agreed_columns_and_types_in_the_agreed_order()
    {
        // arrange, act
        Migrate(EventLog.Symbols);

        // assert
        Assert.Equal(
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
        ], ColumnDefinitions(Log.Events));
    }

    /// <summary>
    /// Unconstrained NUMERIC keeps every digit and the scale it was sent with, so 100000.00 does not become 100000.
    /// </summary>
    [Fact(DisplayName = "Edge case Should keep numerics exactly as sent, digits and scale alike")]
    public void Edge_case_Should_keep_numerics_exactly_as_sent_digits_and_scale_alike()
    {
        // arrange
        Migrate(EventLog.Symbols);

        // act, the API's USDJPY sample, then more digits than a double holds at either end, and a negative zero
        Export(State(25)
            .With("tick_size", "0.001")
            .With("lot_size", "100000.00")
            .With("min_order_size_lots", "0.01")
            .With("swap_long", "-7.123456789012345678")
            .With("max_price_limit", "123456789012345678901234567890.123456789")
            .With("min_price_limit", "0.000000000000000000000000000001")
            .With("swap_short", "-0.0"));

        // assert, the negative zero is the only value to come back changed
        Assert.Equal(
            ["0.001", "100000.00", "0.01", "-7.123456789012345678", "123456789012345678901234567890.123456789", "0.000000000000000000000000000001", "0.0"],
            Values(25, "tick_size", "lot_size", "min_order_size_lots", "swap_long", "max_price_limit", "min_price_limit", "swap_short"));
    }

    [Fact(DisplayName = "Edge case Should take NaN and infinity as prices rather than refuse the batch")]
    public void Edge_case_Should_take_NaN_and_infinity_as_prices_rather_than_refuse_the_batch()
    {
        // arrange
        Migrate(EventLog.Symbols);

        // act
        Export(State(25).With("tick_value", "NaN").With("max_price_limit", "Infinity").With("min_price_limit", "-Infinity"));

        // assert
        Assert.Equal(["NaN", "Infinity", "-Infinity"], Values(25, "tick_value", "max_price_limit", "min_price_limit"));
    }

    /// <summary>
    /// swap_interval is the one enum column held as a SMALLINT.
    /// </summary>
    [Fact(DisplayName = "Edge case Should refuse a swap interval beyond SMALLINT, and the batch with it")]
    public void Edge_case_Should_refuse_a_swap_interval_beyond_SMALLINT_and_the_batch_with_it()
    {
        // arrange
        Migrate(EventLog.Symbols);

        // act, assert, the largest SMALLINT is stored
        Export(State(25).With("swap_interval", 32767));

        Assert.Equal("32767", Value(25, "swap_interval"));

        // and one past it refuses its batch
        AssertRefused(PostgresErrorCodes.NumericValueOutOfRange, () => Export(State(26), State(27).With("swap_interval", 32768)));

        Assert.Equal(["7/25 v1"], Rows());
    }

    /// <summary>
    /// Why symbol_name is not unique: a unique index would refuse this re-announcement and stall the export.
    /// </summary>
    [Fact(DisplayName = "Edge case Should hold two symbols under one name while a resync is converging")]
    public void Edge_case_Should_hold_two_symbols_under_one_name_while_a_resync_is_converging()
    {
        // arrange
        Migrate(EventLog.Symbols);
        Export(Symbol(5, "EURUSD", version: 3));

        // act, 5 was deleted while the sender was down and EURUSD created again as 9
        Export(ResyncStarted());
        Export(Symbol(9, "EURUSD"));

        // assert, both hold the name until the end marker sweeps the old one
        Assert.Equal(["5", "9"], SymbolIdsNamed("EURUSD"));

        Export(ResyncFinished());

        Assert.Equal(["9"], SymbolIdsNamed("EURUSD"));
    }

    /// <summary>
    /// Each sender numbers its own symbols. The wrong join is asserted too, so the fixture keeps reproducing it.
    /// </summary>
    [Fact(DisplayName = "Reading Should need sender_id as well as symbol_id to join trades to their symbol")]
    public void Reading_Should_need_sender_id_as_well_as_symbol_id_to_join_trades_to_their_symbol()
    {
        // arrange, symbol 25 is USDJPY on one sender and XAUUSD on the other, with trades on each
        Migrate(EventLog.Symbols);
        Export(Symbol(25, "USDJPY"), Symbol(25, "XAUUSD", sender: OtherSender));

        Trade(1, symbolId: 25, "USDJPY");
        Trade(2, symbolId: 25, "USDJPY");
        Trade(1, symbolId: 25, "XAUUSD", sender: OtherSender);

        // act, the full key against the id alone
        const string fullKey = "FROM trades t JOIN symbols s ON s.sender_id = t.sender_id AND s.symbol_id = t.symbol_id";
        const string idOnly = "FROM trades t JOIN symbols s ON s.symbol_id = t.symbol_id";

        // assert, three trades have a symbol, and the id alone invents three more, each on the wrong instrument
        Assert.Equal(3, Scalar<long>($"SELECT count(*) {fullKey}"));
        Assert.Equal(0, Scalar<long>($"SELECT count(*) {fullKey} WHERE s.symbol_name <> t.symbol_name"));

        Assert.Equal(6, Scalar<long>($"SELECT count(*) {idOnly}"));
        Assert.Equal(3, Scalar<long>($"SELECT count(*) {idOnly} WHERE s.symbol_name <> t.symbol_name"));
    }

    /// <summary>
    /// The join SCHEMA_REFERENCE gives for the symbol of a trade, a LEFT JOIN because a trade outlives its symbol.
    /// </summary>
    [Fact(DisplayName = "Reading Should keep a trade whose symbol was deleted, without its configuration")]
    public void Reading_Should_keep_a_trade_whose_symbol_was_deleted_without_its_configuration()
    {
        // arrange, a trade on each of two symbols
        Migrate(EventLog.Symbols);
        Export(Symbol(25, "USDJPY").With("profit_currency", "JPY"), Symbol(26, "XAUUSD").With("profit_currency", "USD"));

        Trade(1, symbolId: 25, "USDJPY");
        Trade(2, symbolId: 26, "XAUUSD");

        // act, one of the symbols is deleted
        Export(Deleted(26));

        var trades = Strings("""
                             SELECT t.trade_id || ' ' || t.symbol_name || ': ' || COALESCE(s.profit_currency, 'no symbol')
                             FROM trades t
                                      LEFT JOIN symbols s ON s.sender_id = t.sender_id AND s.symbol_id = t.symbol_id
                             ORDER BY t.trade_id
                             """);

        // assert
        Assert.Equal(["1 USDJPY: JPY", "2 XAUUSD: no symbol"], trades);
    }

    /// <summary>
    /// The symbols row is the configuration now; the trade carries what it executed under, and the log what came between.
    /// </summary>
    [Fact(DisplayName = "Reading Should show the current configuration of a symbol beside a trade, not the one it executed under")]
    public void Reading_Should_show_the_current_configuration_of_a_symbol_beside_a_trade_not_the_one_it_executed_under()
    {
        // arrange, a trade executed while USDJPY traded in lots of 100000
        Migrate(EventLog.Symbols);
        Export(Symbol(25, "USDJPY").With("lot_size", "100000"));

        Trade(1, symbolId: 25, "USDJPY", contractSize: "100000");

        // act, the lot size changes afterwards
        Export(Symbol(25, "USDJPY", version: 2).With("lot_size", "1000"));

        var trade = Strings("""
                            SELECT 'contract size ' || t.contract_size || ', lot size now ' || s.lot_size
                            FROM trades t
                                     JOIN symbols s ON s.sender_id = t.sender_id AND s.symbol_id = t.symbol_id
                            """);

        // assert, the join shows the symbol as it is now and the trade as it executed
        Assert.Equal(["contract size 100000, lot size now 1000"], trade);

        // and the configuration in force at the time is still in the log
        Assert.Equal(["v1: 100000", "v2: 1000"], Strings("SELECT 'v' || version || ': ' || lot_size FROM symbol_events WHERE symbol_id = 25 ORDER BY id"));
    }

    private LogEvent Symbol(long id, string name, long version = 1, long sender = Sender)
    {
        return State(id, version, sender: sender).With("symbol_name", name);
    }

    private void Trade(long tradeId, long symbolId, string symbolName, string contractSize = "0", long sender = Sender)
    {
        InsertRow("trades",
            ("sender_id", $"{sender}"),
            ("trade_id", $"{tradeId}"),
            ("symbol_id", $"{symbolId}"),
            ("symbol_name", $"'{symbolName}'"),
            ("contract_size", contractSize));
    }

    private List<string> SymbolIdsNamed(string name)
    {
        return Strings($"SELECT symbol_id::text FROM symbols WHERE symbol_name = '{name}' ORDER BY symbol_id");
    }
}
