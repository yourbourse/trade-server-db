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

using System.Data.Common;
using ClickHouse.Driver.ADO;
using DbUp.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;
using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;

namespace YB.TradeServer.Db.Migrator.Tests.Execution;

/// <summary>
/// Runs the real migration against the databases started by docker-compose.yml.
/// </summary>
/// <remarks>
/// Every test provisions its own database so the assertions about what a first and a second run apply hold
/// regardless of what the previous run left behind. The database is dropped again on the way out.
/// </remarks>
[Trait("Category", "Integration")]
public class MigrationIntegrationTests : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    private readonly ConfigBuilder configBuilder;
    private readonly ITestOutputHelper output;
    private readonly List<(DatabaseProvider Provider, string ConnectionString, string DatabaseName)> provisionedDatabases = [];

    public MigrationIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output)
    {
        this.configBuilder = configBuilder;
        this.output = output;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (provider, connectionString, databaseName) in provisionedDatabases)
        {
            try
            {
                await DropDatabase(provider, connectionString, databaseName);
            }
            catch (Exception e)
            {
                output.WriteLine($"Unable to drop {databaseName}: {e.Message}");
            }
        }
    }

    [Fact(DisplayName = "ClickHouse migration Should create the schema and be a no-op when run again")]
    public void ClickHouse_migration_Should_create_the_schema_and_be_a_no_op_when_run_again()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.ClickHouse);
        var runner = CreateRunner();

        // act
        var firstRun = runner.Run(request);
        var secondRun = runner.Run(request);

        // assert
        AssertSuccessful(firstRun);
        AssertSuccessful(secondRun);

        Assert.True(CountAppliedScripts(firstRun) > 0, "the first run should apply the schema");
        Assert.Equal(0, CountAppliedScripts(secondRun));

        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "trades"));
        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "export_cursor"));
        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "daily_statements_export"));
        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "daily_statements_order_snapshots"));
        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "daily_statements_position_snapshots"));
        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "schema_versions"));

        // system.tables lists a view alongside the tables
        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "trades_history"));
        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "cash_transfers"));
    }

    [Fact(DisplayName = "ClickHouse migration Should re-apply the re-runnable scripts when forced")]
    public void ClickHouse_migration_Should_re_apply_the_re_runnable_scripts_when_forced()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.ClickHouse);
        var runner = CreateRunner();

        runner.Run(request);

        // act
        var forcedRun = runner.Run(request with { FullReload = true });

        // assert
        AssertSuccessful(forcedRun);
        Assert.True(CountAppliedScripts(forcedRun) > 0, "a forced run should re-apply the post scripts");
    }

    [Fact(DisplayName = "ClickHouse migration Should create the users it is given")]
    public void ClickHouse_migration_Should_create_the_users_it_is_given()
    {
        // arrange
        var userName = $"exporter_{DateTime.UtcNow.Ticks}";

        var request = CreateRequest(DatabaseProvider.ClickHouse) with
        {
            Users = [new() { Access = "exporter", User = userName, Password = "passw0rd" }]
        };

        var runner = CreateRunner();

        // act
        var result = runner.Run(request);

        // assert
        AssertSuccessful(result);

        using var connection = new ClickHouseConnection(request.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand($"SELECT count() FROM system.users WHERE name = '{userName}'");
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact(DisplayName = "Postgres migration Should create the schema and be a no-op when run again")]
    public void Postgres_migration_Should_create_the_schema_and_be_a_no_op_when_run_again()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.Postgres);
        var runner = CreateRunner();

        // act
        var firstRun = runner.Run(request);
        var secondRun = runner.Run(request);

        // assert
        AssertSuccessful(firstRun);
        AssertSuccessful(secondRun);

        Assert.True(CountAppliedScripts(firstRun) > 0, "the first run should apply the schema");
        Assert.Equal(0, CountAppliedScripts(secondRun));

        Assert.True(PostgresTableExists(request.ConnectionString, "trades"));
        Assert.True(PostgresTableExists(request.ConnectionString, "export_cursor"));
        Assert.True(PostgresTableExists(request.ConnectionString, "daily_statements_export"));
        Assert.True(PostgresTableExists(request.ConnectionString, "daily_statements_order_snapshots"));
        Assert.True(PostgresTableExists(request.ConnectionString, "daily_statements_position_snapshots"));
        Assert.True(PostgresTableExists(request.ConnectionString, "schema_versions"));

        // information_schema.tables lists a view alongside the tables
        Assert.True(PostgresTableExists(request.ConnectionString, "trades_history"));
        Assert.True(PostgresTableExists(request.ConnectionString, "cash_transfers"));
    }

    [Fact(DisplayName = "Postgres migration Should create the read path indexes")]
    public void Postgres_migration_Should_create_the_read_path_indexes()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.Postgres);
        var runner = CreateRunner();

        // act
        AssertSuccessful(runner.Run(request));

        // assert
        // the write-path indexes the create scripts ship with
        Assert.True(PostgresIndexExists(request.ConnectionString, "idx_sender_trade_trades"));
        Assert.True(PostgresIndexExists(request.ConnectionString, "idx_time_created_trades"));

        // and the read-path indexes added on top of them
        Assert.True(PostgresIndexExists(request.ConnectionString, "idx_account_time_trades"));
        Assert.True(PostgresIndexExists(request.ConnectionString, "idx_transfers_account_time_trades"));
        Assert.True(PostgresIndexExists(request.ConnectionString, "idx_account_stmt_daily_statements_export"));
        Assert.True(PostgresIndexExists(request.ConnectionString, "idx_account_stmt_daily_statements_order_snapshots"));
        Assert.True(PostgresIndexExists(request.ConnectionString, "idx_account_stmt_daily_statements_position_snapshots"));

        // every index name has to survive PostgreSQL's 63-byte truncation intact, or two of them collide and the
        // second CREATE ... IF NOT EXISTS silently does nothing
        Assert.Empty(PostgresTruncatedIndexNames(request.ConnectionString));
    }

    [Fact(DisplayName = "ClickHouse views Should split every trade type between trades history and cash transfers")]
    public void ClickHouse_views_Should_split_every_trade_type_between_trades_history_and_cash_transfers()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.ClickHouse);
        var runner = CreateRunner();

        AssertSuccessful(runner.Run(request));

        using var connection = new ClickHouseConnection(request.ConnectionString);
        connection.Open();

        foreach (var tradeType in AllTradeTypes)
        {
            using var insert = connection.CreateCommand(ClickHouseTradeInsert(tradeType));
            insert.ExecuteNonQuery();
        }

        // act
        var history = ClickHouseTradeTypes(connection, "trades_history");
        var transfers = ClickHouseTradeTypes(connection, "cash_transfers");

        // assert
        Assert.Equal(TradingTradeTypes, history);
        Assert.Equal(AllTradeTypes.Except(TradingTradeTypes).ToList(), transfers);

        // the collateral row is the only one carrying a symbol
        using var collateral = connection.CreateCommand("SELECT trade_type FROM cash_transfers WHERE is_collateral ORDER BY trade_type");
        Assert.Equal([CollateralTradeType], ReadInts(collateral));
    }

    [Fact(DisplayName = "Postgres views Should split every trade type between trades history and cash transfers")]
    public void Postgres_views_Should_split_every_trade_type_between_trades_history_and_cash_transfers()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.Postgres);
        var runner = CreateRunner();

        AssertSuccessful(runner.Run(request));

        using var connection = new NpgsqlConnection(request.ConnectionString);
        connection.Open();

        foreach (var tradeType in AllTradeTypes)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = PostgresTradeInsert(tradeType);
            insert.ExecuteNonQuery();
        }

        // act
        var history = PostgresTradeTypes(connection, "trades_history");
        var transfers = PostgresTradeTypes(connection, "cash_transfers");

        // assert
        Assert.Equal(TradingTradeTypes, history);
        Assert.Equal(AllTradeTypes.Except(TradingTradeTypes).ToList(), transfers);

        // the collateral row is the only one carrying a symbol
        using var collateral = connection.CreateCommand();
        collateral.CommandText = "SELECT trade_type FROM cash_transfers WHERE is_collateral ORDER BY trade_type";
        Assert.Equal([CollateralTradeType], ReadInts(collateral));
    }

    [Fact(DisplayName = "Postgres migration Should honour the table names passed as variables")]
    public void Postgres_migration_Should_honour_the_table_names_passed_as_variables()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.Postgres);

        request.Variables["trades_table"] = "trades_export";
        request.Variables["cursor_table"] = "trades_export_cursor";
        request.Variables["daily_statements_table"] = "statements_export";
        request.Variables["order_snapshots_table"] = "statements_order_snapshots";
        request.Variables["position_snapshots_table"] = "statements_position_snapshots";

        var runner = CreateRunner();

        // act
        var result = runner.Run(request);

        // assert
        AssertSuccessful(result);

        Assert.True(PostgresTableExists(request.ConnectionString, "trades_export"));
        Assert.True(PostgresTableExists(request.ConnectionString, "trades_export_cursor"));
        Assert.True(PostgresTableExists(request.ConnectionString, "statements_export"));
        Assert.True(PostgresTableExists(request.ConnectionString, "statements_order_snapshots"));
        Assert.True(PostgresTableExists(request.ConnectionString, "statements_position_snapshots"));
        Assert.False(PostgresTableExists(request.ConnectionString, "trades"));
        Assert.False(PostgresTableExists(request.ConnectionString, "daily_statements_export"));
    }

    [Fact(DisplayName = "Postgres migration Should create the users it is given")]
    public void Postgres_migration_Should_create_the_users_it_is_given()
    {
        // arrange
        var userName = $"exporter_{DateTime.UtcNow.Ticks}";

        var request = CreateRequest(DatabaseProvider.Postgres) with
        {
            Users = [new() { Access = "exporter", User = userName, Password = "passw0rd" }]
        };

        var runner = CreateRunner();

        // act
        var result = runner.Run(request);

        // assert
        AssertSuccessful(result);

        using var connection = new NpgsqlConnection(request.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname = @name";
        command.Parameters.Add(new NpgsqlParameter("name", userName));

        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact(DisplayName = "Postgres migration Should drop write access when a user is re-provisioned as read only")]
    public void Postgres_migration_Should_drop_write_access_when_a_user_is_re_provisioned_as_read_only()
    {
        // arrange, the same account is first an exporter and then a reader
        var userName = $"demoted_{DateTime.UtcNow.Ticks}";

        var request = CreateRequest(DatabaseProvider.Postgres) with
        {
            Users = [new() { Access = "exporter", User = userName, Password = "passw0rd" }]
        };

        var runner = CreateRunner();

        AssertSuccessful(runner.Run(request));

        // act
        var readOnlyRun = runner.Run(request with
        {
            Users = [new() { Access = "ro", User = userName, Password = "passw0rd" }]
        });

        // assert
        AssertSuccessful(readOnlyRun);

        Assert.True(PostgresHasPrivilege(request.ConnectionString, userName, "trades", "SELECT"));
        Assert.False(PostgresHasPrivilege(request.ConnectionString, userName, "trades", "INSERT"));
        Assert.False(PostgresHasPrivilege(request.ConnectionString, userName, "export_cursor", "UPDATE"));

        // the daily statement tables are covered by the same revoke and grant lists
        Assert.True(PostgresHasPrivilege(request.ConnectionString, userName, "daily_statements_export", "SELECT"));
        Assert.False(PostgresHasPrivilege(request.ConnectionString, userName, "daily_statements_export", "INSERT"));
        Assert.False(PostgresHasPrivilege(request.ConnectionString, userName, "daily_statements_order_snapshots", "INSERT"));
        Assert.False(PostgresHasPrivilege(request.ConnectionString, userName, "daily_statements_position_snapshots", "INSERT"));
    }

    [Fact(DisplayName = "Postgres migration Should keep the journal entry of a script whose name is a prefix of another")]
    public void Postgres_migration_Should_keep_the_journal_entry_of_a_script_whose_name_is_a_prefix_of_another()
    {
        // arrange, "reader" is a prefix of "reader2", and both are journalled by the same users script
        var reader = $"reader_{DateTime.UtcNow.Ticks}";
        var readerTwo = $"{reader}2";

        var request = CreateRequest(DatabaseProvider.Postgres) with
        {
            Users =
            [
                new() { Access = "ro", User = reader, Password = "passw0rd" },
                new() { Access = "ro", User = readerTwo, Password = "passw0rd" }
            ]
        };

        var runner = CreateRunner();

        AssertSuccessful(runner.Run(request));

        // act, rotating the password of the shorter name rewrites only its own journal entry
        var rotated = runner.Run(request with
        {
            Users =
            [
                new() { Access = "ro", User = reader, Password = "rotated" },
                new() { Access = "ro", User = readerTwo, Password = "passw0rd" }
            ]
        });

        // assert
        AssertSuccessful(rotated);

        var applied = CountAppliedScripts(rotated);

        Assert.Equal(1, applied);
    }

    [Theory(DisplayName = "Migration Should survive a password that would otherwise break out of the literal")]
    [InlineData(DatabaseProvider.Postgres)]
    [InlineData(DatabaseProvider.ClickHouse)]
    public void Migration_Should_survive_a_password_that_would_otherwise_break_out_of_the_literal(DatabaseProvider provider)
    {
        // arrange, a password carrying a quote, a backslash and a statement terminator
        const string hostilePassword = @"p'a\ss'; DROP TABLE trades; --";

        var userName = $"exporter_{DateTime.UtcNow.Ticks}";

        var request = CreateRequest(provider) with
        {
            Users = [new() { Access = "exporter", User = userName, Password = hostilePassword }]
        };

        var runner = CreateRunner();

        // act
        var result = runner.Run(request);

        // assert
        AssertSuccessful(result);

        // the trades table is still standing, so nothing escaped the literal
        Assert.True(provider == DatabaseProvider.Postgres
            ? PostgresTableExists(request.ConnectionString, "trades")
            : ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "trades"));

        // and the password still authenticates, so escaping preserved the value rather than mangling it
        Assert.True(CanLogIn(provider, request.ConnectionString, userName, hostilePassword));
    }

    /// <summary>
    /// Opens a connection as the freshly created user, which only succeeds if the password stored by the
    /// migration is byte for byte the one that was supplied.
    /// </summary>
    private static bool CanLogIn(DatabaseProvider provider, string connectionString, string userName, string password)
    {
        if (provider == DatabaseProvider.Postgres)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Username = userName,
                Password = password
            };

            using var connection = new NpgsqlConnection(builder.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }

        var clickHouseBuilder = new ClickHouseConnectionStringBuilder(connectionString)
        {
            Username = userName,
            Password = password
        };

        using var clickHouseConnection = new ClickHouseConnection(clickHouseBuilder.ToString());
        clickHouseConnection.Open();

        using var clickHouseCommand = clickHouseConnection.CreateCommand("SELECT 1");
        return Convert.ToInt32(clickHouseCommand.ExecuteScalar()) == 1;
    }

    [Fact(DisplayName = "Migration Should refuse a user name that is not a plain identifier")]
    public void Migration_Should_refuse_a_user_name_that_is_not_a_plain_identifier()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.Postgres) with
        {
            Users = [new() { Access = "exporter", User = "evil\"; DROP TABLE trades; --", Password = "passw0rd" }]
        };

        var runner = CreateRunner();

        // act, assert
        var exception = Assert.Throws<SchemaMigrationException>(() => runner.Run(request));
        Assert.Contains("not a valid SQL identifier", exception.Message);
    }

    [Theory(DisplayName = "Migration Should refuse to run without the accounts the users phase provisions")]
    [InlineData(DatabaseProvider.ClickHouse)]
    [InlineData(DatabaseProvider.Postgres)]
    public void Migration_Should_refuse_to_run_without_the_accounts_the_users_phase_provisions(DatabaseProvider provider)
    {
        // arrange
        var request = CreateRequest(provider) with { Users = [] };
        var runner = CreateRunner();

        // act
        var exception = Assert.Throws<SchemaMigrationException>(() => runner.Run(request));

        // assert
        Assert.Contains("--users is required", exception.Message);
        Assert.Contains("exporter", exception.Message);

        // the run has to stop before anything reaches the database, so a missing flag cannot leave a schema
        // behind whose grants were never applied
        Assert.False(DatabaseExists(provider, request.ConnectionString));
    }

    [Fact(DisplayName = "Postgres migration Should report the pending scripts without touching the database on a dry run")]
    public void Postgres_migration_Should_report_the_pending_scripts_without_touching_the_database_on_a_dry_run()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.Postgres);
        var runner = CreateRunner();

        // the dry run needs a database to read the journal from, but must not write to it. No script folder also
        // means no users phase, so this run has no account to provision and passes none
        runner.Run(request with { ScriptFolders = [], Users = [] });

        // act
        var dryRun = runner.Run(request with { DryRun = true });

        // assert
        AssertSuccessful(dryRun);
        Assert.True(CountAppliedScripts(dryRun) > 0, "the dry run should report the pending scripts");
        Assert.False(PostgresTableExists(request.ConnectionString, "trades"));
    }

    private DbUpRunner CreateRunner()
    {
        return new(NullLogger<DbUpRunner>.Instance, new TestDbUpLogProvider(output));
    }

    /// <summary>
    /// The script folders a migration run is given.
    /// </summary>
    /// <remarks>
    /// The shipped clickhouse folder no longer creates the trades and cursor tables - a ClickHouse deployment
    /// provisions those itself and this repository only grants on them by name - so the ClickHouse tests add the
    /// folder holding the table definitions they need to have something to migrate and to grant against.
    /// </remarks>
    private static IList<ScriptConfig> GetScriptFolders(DatabaseProvider provider)
    {
        return provider == DatabaseProvider.ClickHouse
            ? [new(true, Path.Combine("TestScripts", "clickhouse")), new(true, "clickhouse")]
            : [new(true, "psql")];
    }

    private DbMigratorRequest CreateRequest(DatabaseProvider provider)
    {
        var configuredConnectionString = provider == DatabaseProvider.ClickHouse
            ? configBuilder.ClickHouseConnectionString
            : configBuilder.PostgresConnectionString;

        Assert.False(string.IsNullOrWhiteSpace(configuredConnectionString), $"No connection string configured for {provider}. Start docker-compose.yml or set the environment variable.");

        var databaseName = $"trade_integration_{Guid.NewGuid():N}"[..40];
        var connectionString = WithDatabase(provider, configuredConnectionString!, databaseName);

        provisionedDatabases.Add((provider, configuredConnectionString!, databaseName));

        return new(
            provider,
            connectionString,
            databaseName,
            "schema_versions",
            GetScriptFolders(provider),
            configBuilder.RepositoryRoot,
            new Dictionary<string, string>
            {
                ["assembly_version"] = "0.0.0-integration",
                ["database_name"] = databaseName,
                ["trades_table"] = "trades",
                ["cursor_table"] = "export_cursor",
                ["daily_statements_table"] = "daily_statements_export",
                ["order_snapshots_table"] = "daily_statements_order_snapshots",
                ["position_snapshots_table"] = "daily_statements_position_snapshots",
                ["trades_history_view"] = "trades_history",
                ["cash_transfers_view"] = "cash_transfers",
                ["cluster_name"] = "",
                ["cluster_name_suffix"] = "",
                ["merge_tree_engine"] = "MergeTree",
                ["replacing_merge_tree_engine"] = "ReplacingMergeTree"
            },
            false,
            false,
            null,
            // every run has to carry the accounts it provisions, so the request the tests start from carries one.
            // The name is derived from the database so the PostgreSQL roles, which are cluster wide rather than
            // per database, stay unique across the suite
            [new() { Access = "exporter", User = $"{databaseName}_exporter", Password = "passw0rd" }]);
    }

    private static string WithDatabase(DatabaseProvider provider, string connectionString, string databaseName)
    {
        if (provider == DatabaseProvider.ClickHouse)
        {
            return new ClickHouseConnectionStringBuilder(connectionString) { Database = databaseName }.ToString();
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ToString();
    }

    /// <summary>
    /// Whether the database the connection string names has been created. Asked from the server level database so
    /// it can be answered when the target does not exist.
    /// </summary>
    private static bool DatabaseExists(DatabaseProvider provider, string connectionString)
    {
        var databaseName = provider == DatabaseProvider.ClickHouse
            ? new ClickHouseConnectionStringBuilder(connectionString).Database
            : new NpgsqlConnectionStringBuilder(connectionString).Database;

        if (provider == DatabaseProvider.ClickHouse)
        {
            using var clickHouseConnection = new ClickHouseConnection(WithDatabase(provider, connectionString, "system"));
            clickHouseConnection.Open();

            using var clickHouseCommand = clickHouseConnection.CreateCommand($"SELECT count() FROM system.databases WHERE name = '{databaseName}'");
            return Convert.ToInt32(clickHouseCommand.ExecuteScalar()) > 0;
        }

        using var connection = new NpgsqlConnection(WithDatabase(provider, connectionString, "postgres"));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("name", databaseName!);

        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static async Task DropDatabase(DatabaseProvider provider, string connectionString, string databaseName)
    {
        DbConnection connection = provider == DatabaseProvider.ClickHouse
            ? new ClickHouseConnection(WithDatabase(provider, connectionString, "system"))
            : new NpgsqlConnection(WithDatabase(provider, connectionString, "postgres"));

        await using (connection)
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();

            command.CommandText = provider == DatabaseProvider.ClickHouse
                ? $"DROP DATABASE IF EXISTS {databaseName}"
                : $@"DROP DATABASE IF EXISTS ""{databaseName}"" WITH (FORCE)";

            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Every value of the trade server's TradeType enum, kBuy through kCreditBonus.
    /// </summary>
    private static readonly List<int> AllTradeTypes = [.. Enumerable.Range(0, 22)];

    /// <summary>
    /// The four the trades history view keeps: kBuy, kSell, kBuyCancelled and kSellCancelled. Everything else
    /// is a cash movement and belongs to the other view.
    /// </summary>
    private static readonly List<int> TradingTradeTypes = [0, 1, 14, 15];

    /// <summary>
    /// The one trade type the tests below give a symbol to, standing in for a collateral transfer - the trade
    /// server resolves a symbol only when the transfer currency differs from the account currency.
    /// </summary>
    private const int CollateralTradeType = 5;

    /// <summary>
    /// The columns of a trades row, in the order the two INSERT helpers supply their values.
    /// </summary>
    private const string TradeColumns =
        "sender_id, group_name, symbol_name, trade_id, time_created, time_modified, trading_account_id, group_id, " +
        "trade_type, executed_price, volume, order_id, position_id, profit, commission, swaps, fee, position_price, " +
        "profit_conversion_rate, contract_size, notional_value_in_original_currency, " +
        "notional_value_in_account_currency, tick_value, tick_size, market_bid, market_ask, symbol_id, group_markup, " +
        "side, entry_type, reason, price_decimal_places, account_currency_decimal_places, comment, agent_commission, " +
        "notional_value_in_usd";

    /// <summary>
    /// One trades row per trade type, carrying a symbol only for <see cref="CollateralTradeType"/>. The trade
    /// type doubles as the trade id, which is what keeps the rows distinct under either engine's deduplication.
    /// </summary>
    private static string TradeValues(int tradeType, string now)
    {
        var symbolId = tradeType == CollateralTradeType ? 77 : 0;

        return $"1, 'g', 'EUR', {tradeType}, {now}, {now}, 7, 3, " +
               $"{tradeType}, 0, 0, 0, 0, 0, 0, 0, 0, 0, " +
               "1, 0, 100, " +
               "100, 0, 0, 0, 0, " +
               $"{symbolId}, 0, " +
               "0, 0, 1, 5, 2, '', 0, " +
               "100";
    }

    private static string ClickHouseTradeInsert(int tradeType) =>
        $"INSERT INTO trades ({TradeColumns}) VALUES ({TradeValues(tradeType, "now64(6)")})";

    private static string PostgresTradeInsert(int tradeType) =>
        $"INSERT INTO trades ({TradeColumns}) VALUES ({TradeValues(tradeType, "now()")})";

    private static List<int> ClickHouseTradeTypes(ClickHouseConnection connection, string viewName)
    {
        using var command = connection.CreateCommand($"SELECT trade_type FROM {viewName} ORDER BY trade_type");

        return ReadInts(command);
    }

    private static List<int> PostgresTradeTypes(NpgsqlConnection connection, string viewName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT trade_type FROM {viewName} ORDER BY trade_type";

        return ReadInts(command);
    }

    private static List<int> ReadInts(DbCommand command)
    {
        using var reader = command.ExecuteReader();

        var values = new List<int>();

        while (reader.Read())
        {
            values.Add(Convert.ToInt32(reader.GetValue(0)));
        }

        return values;
    }

    private static bool ClickHouseTableExists(string connectionString, string databaseName, string tableName)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand($"SELECT count() FROM system.tables WHERE database = '{databaseName}' AND name = '{tableName}'");

        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static bool PostgresIndexExists(string connectionString, string indexName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = @name";
        command.Parameters.Add(new NpgsqlParameter("name", indexName));

        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    /// <summary>
    /// The index names that reached PostgreSQL's 63-byte identifier limit and were therefore truncated.
    /// </summary>
    /// <remarks>
    /// A truncated name is not an error on its own, but two indexes truncating to the same name are: the second
    /// <c>CREATE INDEX IF NOT EXISTS</c> then sees a name that already exists and quietly does nothing, leaving
    /// the index missing. Flagging any name at the limit catches that before it can happen.
    /// </remarks>
    private static List<string> PostgresTruncatedIndexNames(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND length(indexname) >= 63";

        using var reader = command.ExecuteReader();

        var names = new List<string>();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static bool PostgresTableExists(string connectionString, string tableName)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name";
        command.Parameters.Add(new NpgsqlParameter("name", tableName));

        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static bool PostgresHasPrivilege(string connectionString, string userName, string tableName, string privilege)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT has_table_privilege(@user, @table, @privilege)";
        command.Parameters.Add(new NpgsqlParameter("user", userName));
        command.Parameters.Add(new NpgsqlParameter("table", tableName));
        command.Parameters.Add(new NpgsqlParameter("privilege", privilege));

        return Convert.ToBoolean(command.ExecuteScalar());
    }

    private static int CountAppliedScripts(IEnumerable<(string SubFolder, DatabaseUpgradeResult Result)> results)
    {
        return results.Sum(result => result.Result.Scripts.Count());
    }

    private static void AssertSuccessful(IEnumerable<(string SubFolder, DatabaseUpgradeResult Result)> results)
    {
        foreach (var (subFolder, result) in results)
        {
            Assert.True(result.Successful, $"{subFolder} failed on '{result.ErrorScript?.Name}': {result.Error}");
        }
    }
}
