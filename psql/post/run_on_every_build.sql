-- Runs on every migration. The content never changes, so the MD5 in the journal stays the same and the entry
-- is simply refreshed - it exists to make the applied version visible in the migration log.

SELECT 'Deployment complete. Database: ##database_name##, Trades table: ##trades_table##, Daily statements table: ##daily_statements_table##, Order snapshots table: ##order_snapshots_table##, Position snapshots table: ##position_snapshots_table##, Trading account events table: ##trading_account_events_table##, Trading accounts table: ##trading_accounts_table##, Client events table: ##client_events_table##, Clients table: ##clients_table##, Cursor table: ##cursor_table##, Version: ##assembly_version##';
