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
        Assert.True(ClickHouseTableExists(request.ConnectionString, request.DatabaseName, "schema_versions"));
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
        Assert.True(PostgresTableExists(request.ConnectionString, "schema_versions"));
    }

    [Fact(DisplayName = "Postgres migration Should honour the table names passed as variables")]
    public void Postgres_migration_Should_honour_the_table_names_passed_as_variables()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.Postgres);

        request.Variables["trades_table"] = "trades_export";
        request.Variables["cursor_table"] = "trades_export_cursor";

        var runner = CreateRunner();

        // act
        var result = runner.Run(request);

        // assert
        AssertSuccessful(result);

        Assert.True(PostgresTableExists(request.ConnectionString, "trades_export"));
        Assert.True(PostgresTableExists(request.ConnectionString, "trades_export_cursor"));
        Assert.False(PostgresTableExists(request.ConnectionString, "trades"));
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

    [Fact(DisplayName = "Postgres migration Should report the pending scripts without touching the database on a dry run")]
    public void Postgres_migration_Should_report_the_pending_scripts_without_touching_the_database_on_a_dry_run()
    {
        // arrange
        var request = CreateRequest(DatabaseProvider.Postgres);
        var runner = CreateRunner();

        // the dry run needs a database to read the journal from, but must not write to it
        runner.Run(request with { ScriptFolders = [] });

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
                ["cluster_name"] = "",
                ["cluster_name_suffix"] = "",
                ["merge_tree_engine"] = "MergeTree",
                ["replacing_merge_tree_engine"] = "ReplacingMergeTree"
            },
            false,
            false,
            null,
            new List<DatabaseUser>());
    }

    private static string WithDatabase(DatabaseProvider provider, string connectionString, string databaseName)
    {
        if (provider == DatabaseProvider.ClickHouse)
        {
            return new ClickHouseConnectionStringBuilder(connectionString) { Database = databaseName }.ToString();
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ToString();
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

    private static bool ClickHouseTableExists(string connectionString, string databaseName, string tableName)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand($"SELECT count() FROM system.tables WHERE database = '{databaseName}' AND name = '{tableName}'");

        return Convert.ToInt32(command.ExecuteScalar()) == 1;
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
