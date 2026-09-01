-- Indexes for the read path.
--
-- The indexes created alongside the tables serve the export: a unique key per table that turns an at-least-once
-- redelivery into deduplication, and time_created on trades. Every one of them leads with sender_id, which a
-- report never filters on - a deployment usually has one sender - so none of them can serve the query the ro
-- role actually runs: "this account, over this period". That was a sequential scan of the whole table.
--
-- These five close that gap. Each is a composite leading with trading_account_id, because equality on the
-- account followed by a range on time is the shape of nearly every report; a lone trading_account_id index
-- would still leave the time range to a filter. No DESC: PostgreSQL scans an ascending index backwards just as
-- cheaply, so "the latest N" is served without it.
--
-- Indexes are not free on the write path - the exporter inserts into trades continuously - so this stops at the
-- access paths the shipped views and the statement tables need. Per-symbol, per-group and per-position
-- reporting are all plausible next candidates, deliberately left out until a real workload asks for them
-- rather than guessed at here.
--
-- IF NOT EXISTS throughout, so this adopts an index a deployment already built by hand - see the note on large
-- tables below.
--
-- Naming: what the index covers comes before the table name, for the same reason as in the create scripts.
-- PostgreSQL truncates an identifier to 63 bytes, and a long configured table name would otherwise leave two
-- indexes with the same truncated name and silently skip the second CREATE ... IF NOT EXISTS.
--
--
-- ON A LARGE, LIVE TABLE, BUILD THESE BY HAND FIRST.
--
-- CREATE INDEX takes an ACCESS EXCLUSIVE lock and holds it for the whole build, which blocks the exporter's
-- inserts until it finishes. CREATE INDEX CONCURRENTLY is the usual escape and it cannot be used here: the
-- migrator runs every PostgreSQL script inside one transaction, and PostgreSQL refuses CONCURRENTLY inside a
-- transaction block. A ----break---- does not help - it splits the script into several commands within the
-- same transaction, not out of it.
--
-- So on a table already holding a serious number of rows, build them concurrently outside the migrator, then
-- run the migration: every statement below is IF NOT EXISTS and adopts what is already there.
--
--   CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_account_time_trades ON trades (trading_account_id, time_created);
--   ...one per index below, then ./migrate.sh
--
-- Check for a failed concurrent build before trusting it - a cancelled CONCURRENTLY leaves an invalid index
-- behind that the planner ignores and IF NOT EXISTS will not replace:
--
--   SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid;
--
-- On an empty or small database - a new deployment - none of this matters and the migration builds them
-- outright.

-- Trades for an account over a period: the account statement, the trade blotter, the trades behind a statement
-- line. Serves ##trades_history_view## as well, which adds only a trade_type filter on top.
CREATE INDEX IF NOT EXISTS idx_account_time_##trades_table##
    ON ##trades_table## (trading_account_id, time_created);

-- The same query against ##cash_transfers_view##. The partial predicate is written exactly as the view's WHERE
-- clause so the planner can match it; the index then holds only the accounting records, which are a small
-- fraction of a trades table, and a per-account transfer history reads a fraction of the pages the full index
-- above would.
CREATE INDEX IF NOT EXISTS idx_transfers_account_time_##trades_table##
    ON ##trades_table## (trading_account_id, time_created)
    WHERE trade_type NOT IN (0, 1, 14, 15);

-- An account's statement history. The unique index on this table leads with sender_id and carries
-- trading_account_id third, so it cannot serve a filter on the account alone.
CREATE INDEX IF NOT EXISTS idx_account_stmt_##daily_statements_table##
    ON ##daily_statements_table## (trading_account_id, time_statement);

-- The working orders behind one of those statements. Same reasoning: trading_account_id is not in the unique
-- index at all, so pulling an account's snapshot rows meant scanning the table.
CREATE INDEX IF NOT EXISTS idx_account_stmt_##order_snapshots_table##
    ON ##order_snapshots_table## (trading_account_id, time_statement);

-- The open positions behind one of those statements.
CREATE INDEX IF NOT EXISTS idx_account_stmt_##position_snapshots_table##
    ON ##position_snapshots_table## (trading_account_id, time_statement);
