-- Symbol config events from the trade server; ##symbols_table## is derived from it.
-- Order by id (a batch shares one time_inserted). Unused columns hold '' / 0 / false / epoch, never NULL.
-- Enums are SymbolConfig ordinals. Column order is the agreed schema. No dedup key: redeliveries add rows.

CREATE TABLE IF NOT EXISTS ##symbol_events_table##
(
    id                                      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                           TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                               BIGINT                   NOT NULL,

    -- event metadata
    -- 0 state, 1 deletion, 2 resync started, 3 resync finished
    event_type                              SMALLINT                 NOT NULL,
    -- 0 on markers
    symbol_id                              BIGINT                   NOT NULL,

    -- quoting and filtering
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

    -- pricing
    max_price_limit                         NUMERIC                  NOT NULL,
    min_price_limit                         NUMERIC                  NOT NULL,
    tick_value                              NUMERIC                  NOT NULL,
    tick_size                               NUMERIC                  NOT NULL,
    lot_size                                NUMERIC                  NOT NULL,

    -- currency decimal places
    base_currency_decimal_places            INTEGER                  NOT NULL,
    profit_currency_decimal_places          INTEGER                  NOT NULL,
    margin_currency_decimal_places          INTEGER                  NOT NULL,

    -- trading modes
    trade_mode                              INTEGER                  NOT NULL,
    margin_calc_mode                        INTEGER                  NOT NULL,
    profit_calc_mode                        INTEGER                  NOT NULL,

    -- order size limits, in lots
    min_order_size_lots                     NUMERIC                  NOT NULL,
    max_order_size_lots                     NUMERIC                  NOT NULL,
    order_size_increment_lots               NUMERIC                  NOT NULL,
    max_position_lots                       NUMERIC                  NOT NULL,

    -- order settings
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

    -- margin amounts
    initial_margin                          NUMERIC                  NOT NULL,
    maintenance_margin                      NUMERIC                  NOT NULL,
    hedged_margin                           NUMERIC                  NOT NULL,

    -- initial margin rates
    initial_buy_margin_rate                 NUMERIC                  NOT NULL,
    initial_sell_margin_rate                NUMERIC                  NOT NULL,
    initial_buy_limit_margin_rate           NUMERIC                  NOT NULL,
    initial_sell_limit_margin_rate          NUMERIC                  NOT NULL,
    initial_buy_stop_margin_rate            NUMERIC                  NOT NULL,
    initial_sell_stop_margin_rate           NUMERIC                  NOT NULL,
    initial_buy_stop_limit_margin_rate      NUMERIC                  NOT NULL,
    initial_sell_stop_limit_margin_rate     NUMERIC                  NOT NULL,

    -- maintenance margin rates
    maintenance_buy_margin_rate             NUMERIC                  NOT NULL,
    maintenance_sell_margin_rate            NUMERIC                  NOT NULL,
    maintenance_buy_limit_margin_rate       NUMERIC                  NOT NULL,
    maintenance_sell_limit_margin_rate      NUMERIC                  NOT NULL,
    maintenance_buy_stop_margin_rate        NUMERIC                  NOT NULL,
    maintenance_sell_stop_margin_rate       NUMERIC                  NOT NULL,
    maintenance_buy_stop_limit_margin_rate  NUMERIC                  NOT NULL,
    maintenance_sell_stop_limit_margin_rate NUMERIC                  NOT NULL,

    -- margin percentage
    percentage                              NUMERIC                  NOT NULL,

    -- identifiers and currencies
    symbol_name                             VARCHAR(32)              NOT NULL DEFAULT '',
    base_currency                           VARCHAR(16)              NOT NULL DEFAULT '',
    profit_currency                         VARCHAR(16)              NOT NULL DEFAULT '',
    margin_currency                         VARCHAR(16)              NOT NULL DEFAULT '',

    -- flags
    margin_use_larger_leg                   BOOLEAN                  NOT NULL,
    recalculate_margin_rate_at_eod          BOOLEAN                  NOT NULL,
    margin_check_on_sl_tp                   BOOLEAN                  NOT NULL,
    allow_fok                               BOOLEAN                  NOT NULL,
    allow_ioc                               BOOLEAN                  NOT NULL,
    allow_gtc                               BOOLEAN                  NOT NULL,
    allow_gtd                               BOOLEAN                  NOT NULL,
    allow_day_orders                        BOOLEAN                  NOT NULL,
    allow_valid_for_milliseconds            BOOLEAN                  NOT NULL,

    -- swaps
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

    -- versioning and validity window
    -- 0 on deletions and markers
    version                                 BIGINT                   NOT NULL,
    start_date_time                         TIMESTAMP WITH TIME ZONE NOT NULL,
    end_date_time                           TIMESTAMP WITH TIME ZONE NOT NULL,

    -- classification and description
    symbol_path                             VARCHAR(128)             NOT NULL DEFAULT '',
    description                             VARCHAR(64)              NOT NULL DEFAULT '',
    exchange                                VARCHAR(64)              NOT NULL DEFAULT '',
    country                                 VARCHAR(64)              NOT NULL DEFAULT '',
    webpage                                 VARCHAR(256)             NOT NULL DEFAULT '',
    underlying_asset                        VARCHAR(32)              NOT NULL DEFAULT '',
    isin                                    VARCHAR(16)              NOT NULL DEFAULT '',
    cfi                                     VARCHAR(8)               NOT NULL DEFAULT '',

    -- liquidity and markup
    liquidity_margin_rate                   NUMERIC                  NOT NULL,
    bid_markup                              INTEGER                  NOT NULL,
    ask_markup                              INTEGER                  NOT NULL,

    -- swap interval and currency conversion
    swap_interval                           SMALLINT                 NOT NULL,
    eligible_for_currency_conversion        BOOLEAN                  NOT NULL,

    -- intraday swap timing and price corridor
    swap_intraday_starting_time             INTEGER                  NOT NULL,
    price_corridor_threshold                NUMERIC                  NOT NULL,

    -- industry classification and delayed feed
    industry_tag                            VARCHAR(128)             NOT NULL DEFAULT '',
    delay_interval_minutes                  INTEGER                  NOT NULL,
    delay_mode                              INTEGER                  NOT NULL
);

-- prefix first, so 63-byte truncation cannot make two names collide
CREATE INDEX IF NOT EXISTS idx_symbol_row_##symbol_events_table##
    ON ##symbol_events_table## (sender_id, symbol_id, id);

-- the end marker's watermark lookup
CREATE INDEX IF NOT EXISTS idx_markers_##symbol_events_table##
    ON ##symbol_events_table## (sender_id, event_type, id)
    WHERE event_type IN (2, 3);
