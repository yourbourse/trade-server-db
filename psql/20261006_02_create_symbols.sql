-- Current state of every live symbol, kept by the trigger in views/symbols_projection.sql.
-- No FK on source_event_row_id: events age out first. No unique symbol_name: mid-resync, a deleted symbol
-- and its same-named replacement both have a row until the end marker sweeps.

CREATE TABLE IF NOT EXISTS ##symbols_table##
(
    sender_id                               BIGINT                   NOT NULL,
    symbol_id                               BIGINT                   NOT NULL,

    throttling_price_update_count           INTEGER                  NOT NULL,
    spread_balance                          INTEGER                  NOT NULL,
    decimal_places                          INTEGER                  NOT NULL,
    allow_negative_prices                   BOOLEAN                  NOT NULL,
    allow_realtime_quotes                   BOOLEAN                  NOT NULL,
    market_depth                            INTEGER                  NOT NULL,
    min_spread_filter                       INTEGER                  NOT NULL,
    max_spread_filter                       INTEGER                  NOT NULL,
    soft_filter                             INTEGER                  NOT NULL,
    soft_filter_tick_count                  INTEGER                  NOT NULL,
    hard_filter                             INTEGER                  NOT NULL,
    hard_filter_tick_count                  INTEGER                  NOT NULL,
    discard_filter                          INTEGER                  NOT NULL,
    gap_diff                                INTEGER                  NOT NULL,
    gap_tick_count                          INTEGER                  NOT NULL,

    max_price_limit                         NUMERIC                  NOT NULL,
    min_price_limit                         NUMERIC                  NOT NULL,
    tick_value                              NUMERIC                  NOT NULL,
    tick_size                               NUMERIC                  NOT NULL,
    lot_size                                NUMERIC                  NOT NULL,

    base_currency_decimal_places            INTEGER                  NOT NULL,
    profit_currency_decimal_places          INTEGER                  NOT NULL,
    margin_currency_decimal_places          INTEGER                  NOT NULL,

    trade_mode                              INTEGER                  NOT NULL,
    margin_calc_mode                        INTEGER                  NOT NULL,
    profit_calc_mode                        INTEGER                  NOT NULL,

    min_order_size_lots                     NUMERIC                  NOT NULL,
    max_order_size_lots                     NUMERIC                  NOT NULL,
    order_size_increment_lots               NUMERIC                  NOT NULL,
    max_position_lots                       NUMERIC                  NOT NULL,

    stops_level                             INTEGER                  NOT NULL,
    freeze_level                            INTEGER                  NOT NULL,
    allow_market_orders                     BOOLEAN                  NOT NULL,
    allow_limit_orders                      BOOLEAN                  NOT NULL,
    allow_stop_orders                       BOOLEAN                  NOT NULL,
    allow_stop_limit_orders                 BOOLEAN                  NOT NULL,
    allow_stop_loss_orders                  BOOLEAN                  NOT NULL,
    allow_take_profit_orders                BOOLEAN                  NOT NULL,
    allow_close_by_orders                   BOOLEAN                  NOT NULL,
    chart_mode                              INTEGER                  NOT NULL,
    quotes_timeout_seconds                  INTEGER                  NOT NULL,

    initial_margin                          NUMERIC                  NOT NULL,
    maintenance_margin                      NUMERIC                  NOT NULL,
    hedged_margin                           NUMERIC                  NOT NULL,

    initial_buy_margin_rate                 NUMERIC                  NOT NULL,
    initial_sell_margin_rate                NUMERIC                  NOT NULL,
    initial_buy_limit_margin_rate           NUMERIC                  NOT NULL,
    initial_sell_limit_margin_rate          NUMERIC                  NOT NULL,
    initial_buy_stop_margin_rate            NUMERIC                  NOT NULL,
    initial_sell_stop_margin_rate           NUMERIC                  NOT NULL,
    initial_buy_stop_limit_margin_rate      NUMERIC                  NOT NULL,
    initial_sell_stop_limit_margin_rate     NUMERIC                  NOT NULL,

    maintenance_buy_margin_rate             NUMERIC                  NOT NULL,
    maintenance_sell_margin_rate            NUMERIC                  NOT NULL,
    maintenance_buy_limit_margin_rate       NUMERIC                  NOT NULL,
    maintenance_sell_limit_margin_rate      NUMERIC                  NOT NULL,
    maintenance_buy_stop_margin_rate        NUMERIC                  NOT NULL,
    maintenance_sell_stop_margin_rate       NUMERIC                  NOT NULL,
    maintenance_buy_stop_limit_margin_rate  NUMERIC                  NOT NULL,
    maintenance_sell_stop_limit_margin_rate NUMERIC                  NOT NULL,

    percentage                              NUMERIC                  NOT NULL,

    symbol_name                             VARCHAR(32)              NOT NULL,
    base_currency                           VARCHAR(16)              NOT NULL,
    profit_currency                         VARCHAR(16)              NOT NULL,
    margin_currency                         VARCHAR(16)              NOT NULL,

    margin_use_larger_leg                   BOOLEAN                  NOT NULL,
    recalculate_margin_rate_at_eod          BOOLEAN                  NOT NULL,
    margin_check_on_sl_tp                   BOOLEAN                  NOT NULL,
    allow_fok                               BOOLEAN                  NOT NULL,
    allow_ioc                               BOOLEAN                  NOT NULL,
    allow_gtc                               BOOLEAN                  NOT NULL,
    allow_gtd                               BOOLEAN                  NOT NULL,
    allow_day_orders                        BOOLEAN                  NOT NULL,
    allow_valid_for_milliseconds            BOOLEAN                  NOT NULL,

    swap_long                               NUMERIC                  NOT NULL,
    swap_short                              NUMERIC                  NOT NULL,
    swap_rate_sunday                        NUMERIC                  NOT NULL,
    swap_rate_monday                        NUMERIC                  NOT NULL,
    swap_rate_tuesday                       NUMERIC                  NOT NULL,
    swap_rate_wednesday                     NUMERIC                  NOT NULL,
    swap_rate_thursday                      NUMERIC                  NOT NULL,
    swap_rate_friday                        NUMERIC                  NOT NULL,
    swap_rate_saturday                      NUMERIC                  NOT NULL,
    swap_year_days                          INTEGER                  NOT NULL,
    swap_mode                               INTEGER                  NOT NULL,

    version                                 BIGINT                   NOT NULL,
    start_date_time                         TIMESTAMP WITH TIME ZONE NOT NULL,
    end_date_time                           TIMESTAMP WITH TIME ZONE NOT NULL,

    symbol_path                             VARCHAR(128)             NOT NULL,
    description                             VARCHAR(64)              NOT NULL,
    exchange                                VARCHAR(64)              NOT NULL,
    country                                 VARCHAR(64)              NOT NULL,
    webpage                                 VARCHAR(256)             NOT NULL,
    underlying_asset                        VARCHAR(32)              NOT NULL,
    isin                                    VARCHAR(16)              NOT NULL,
    cfi                                     VARCHAR(8)               NOT NULL,

    liquidity_margin_rate                   NUMERIC                  NOT NULL,
    bid_markup                              INTEGER                  NOT NULL,
    ask_markup                              INTEGER                  NOT NULL,

    swap_interval                           SMALLINT                 NOT NULL,
    eligible_for_currency_conversion        BOOLEAN                  NOT NULL,

    swap_intraday_starting_time             INTEGER                  NOT NULL,
    price_corridor_threshold                NUMERIC                  NOT NULL,

    industry_tag                            VARCHAR(128)             NOT NULL,
    delay_interval_minutes                  INTEGER                  NOT NULL,
    delay_mode                              INTEGER                  NOT NULL,

    source_event_row_id                     BIGINT                   NOT NULL,
    time_applied                            TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),

    PRIMARY KEY (sender_id, symbol_id)
);
