-- End of day statements exported by the trade server.
--
-- One row per account per statement run: the balances, equity, margin and the transaction totals of the day, as
-- they stood at the scheduled end of day time carried in time_statement. Like trades the export is append-only
-- and at-least-once, and the unique index on (sender_id, time_statement, trading_account_id) is what turns a
-- resend into deduplication instead of a duplicate row.
--
-- The statement is exported alongside a snapshot of every working order and every open position of the account
-- - see 20260101_04 and 20260101_05. All three share the same time_statement, which is what ties a statement to
-- its orders and positions.
--
-- IF NOT EXISTS is used throughout so the migrator can be pointed at a database that was provisioned by hand
-- from the Export Setup guide.

CREATE TABLE IF NOT EXISTS ##daily_statements_table##
(
    id                              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    time_inserted                   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
    sender_id                       BIGINT           NOT NULL,
    time_statement                  TIMESTAMP WITH TIME ZONE NOT NULL,
    group_name                      VARCHAR(128)     NOT NULL,
    trading_account_id              BIGINT           NOT NULL,
    group_id                        BIGINT           NOT NULL,
    statement_client_name           VARCHAR(160)     NOT NULL,
    account_currency                VARCHAR(16)      NOT NULL,
    email                           VARCHAR(64)      NOT NULL,
    current_balance                 NUMERIC          NOT NULL,
    prev_day_balance                NUMERIC          NOT NULL,
    current_credit                  NUMERIC          NOT NULL,
    prev_day_credit                 NUMERIC          NOT NULL,
    current_floating_profit         NUMERIC          NOT NULL,
    lifetime_realized_profit        NUMERIC          NOT NULL,
    current_equity                  NUMERIC          NOT NULL,
    prev_day_equity                 NUMERIC          NOT NULL,
    current_margin                  NUMERIC          NOT NULL,
    current_collateral              NUMERIC          NOT NULL,
    prev_day_collateral             NUMERIC          NOT NULL,
    open_positions_swaps            NUMERIC          NOT NULL,
    closed_positions_swaps          NUMERIC          NOT NULL,
    commissions                     NUMERIC          NOT NULL,
    agent_commissions               NUMERIC          NOT NULL,
    fees                            NUMERIC          NOT NULL,
    interest                        NUMERIC          NOT NULL,
    deposits                        NUMERIC          NOT NULL,
    withdrawals                     NUMERIC          NOT NULL,
    credit_transactions             NUMERIC          NOT NULL,
    adjustment_transactions         NUMERIC          NOT NULL,
    balance_bonus_transactions      NUMERIC          NOT NULL,
    credit_bonus_transactions       NUMERIC          NOT NULL,
    dividend_transactions           NUMERIC          NOT NULL,
    tax                             NUMERIC          NOT NULL,
    stopout_compensations           NUMERIC          NOT NULL,
    stopout_credit_adjustments      NUMERIC          NOT NULL,
    volume_in_usd                   NUMERIC          NOT NULL,
    volume_in_account_currency      NUMERIC          NOT NULL,
    realized_profit                 NUMERIC          NOT NULL,
    account_currency_decimal_places SMALLINT         NOT NULL
);

-- what the index covers comes before the table name, for the same reason as in 20260101_01_create_trades.sql:
-- PostgreSQL truncates an identifier to 63 bytes, and a long configured table name would otherwise leave two
-- indexes with the same truncated name. "stmt" keeps the default names inside the limit - the statement tables
-- have the longest names in the schema.
CREATE UNIQUE INDEX IF NOT EXISTS idx_sender_stmt_account_##daily_statements_table##
    ON ##daily_statements_table## (sender_id, time_statement, trading_account_id);
