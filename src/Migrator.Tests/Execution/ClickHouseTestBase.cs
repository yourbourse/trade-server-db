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
/// What the ClickHouse suites share: a throwaway database per request, migrating it, the catalog, the accounts and
/// running SQL.
/// </summary>
/// <remarks>
/// Each request gets its own database, users and roles, dropped again when the test ends.
/// </remarks>
public abstract class ClickHouseTestBase(ConfigBuilder configBuilder, ITestOutputHelper output) : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    protected const ulong Sender = 7;
    protected const ulong OtherSender = 8;

    protected const int UnknownTable = 60;
    protected const int ArgumentOutOfBound = 69;
    protected const int IllegalFinal = 181;
    protected const int IndexNotUsed = 277;
    protected const int DecimalOverflow = 407;
    protected const int AccessDenied = 497;
    protected const int AuthenticationFailed = 516;

    private readonly List<(string ConnectionString, string DatabaseName)> provisionedDatabases = [];

    /// <summary>
    /// The instant a test's rows are stamped around: an hour back, in whole seconds, well inside any retention.
    /// </summary>
    private readonly DateTime recent = DateTime.UnixEpoch.AddSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddHours(-1);

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

    // ---------------------------------------------------------------------------------------------------
    // Migrating
    // ---------------------------------------------------------------------------------------------------

    protected DbUpRunner CreateRunner()
    {
        return new(NullLogger<DbUpRunner>.Instance, new TestDbUpLogProvider(output));
    }

    /// <summary>
    /// A request for a new database named after the suite, with an exporter and a reader account.
    /// </summary>
    /// <param name="databasePrefix">The start of the database name, which tells the suites' leftovers apart.</param>
    /// <param name="variables">--variables entries, such as <c>ticks_ttl_days=7</c>.</param>
    protected DbMigratorRequest CreateClickHouseRequest(string databasePrefix, params string[] variables)
    {
        var configured = configBuilder.ClickHouseConnectionString;

        Assert.False(string.IsNullOrWhiteSpace(configured), "No ClickHouse connection string configured. Start docker-compose.yml or set ClickHouse__ConnectionString.");

        var databaseName = $"{databasePrefix}{Guid.NewGuid():N}"[..40];

        provisionedDatabases.Add((configured!, databaseName));

        return new(
            DatabaseProvider.ClickHouse,
            WithDatabase(configured!, databaseName),
            databaseName,
            "schema_versions",
            [new(true, "clickhouse")],
            configBuilder.RepositoryRoot,
            ClickHouseVariables(databaseName, variables),
            false,
            false,
            null,
            [
                new() { Access = "exporter", User = $"{databaseName}_exporter", Password = "passw0rd" },
                new() { Access = "ro", User = $"{databaseName}_reader", Password = "passw0rd" }
            ]);
    }

    /// <summary>
    /// The CLI's own variables, so new ones reach these tests automatically, with the given --variables applied.
    /// </summary>
    protected static Dictionary<string, string> ClickHouseVariables(string databaseName, params string[] variables)
    {
        return MigrationHandler.BuildVariables(DatabaseProvider.ClickHouse, databaseName, null,
            new() { Variables = variables.Length == 0 ? null : variables });
    }

    protected static void AssertSuccessful(IEnumerable<(string SubFolder, DatabaseUpgradeResult Result)> results)
    {
        foreach (var (subFolder, result) in results)
        {
            Assert.True(result.Successful, $"{subFolder} failed on '{result.ErrorScript?.Name}': {result.Error}");
        }
    }

    protected static void CreateDatabase(DbMigratorRequest request)
    {
        Execute(WithDatabase(request.ConnectionString, "system"), $"CREATE DATABASE {request.DatabaseName}");
    }

    // ---------------------------------------------------------------------------------------------------
    // Time
    // ---------------------------------------------------------------------------------------------------

    protected DateTime At(double seconds)
    {
        return recent.AddSeconds(seconds);
    }

    protected static DateTime DaysAgo(int days)
    {
        return DateTime.UtcNow.AddDays(-days);
    }

    protected static DateTime HoursAgo(int hours)
    {
        return DateTime.UtcNow.AddHours(-hours);
    }

    protected static DateTime Utc(string time)
    {
        return DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    /// <summary>
    /// A time as ClickHouse prints a DateTime64(6).
    /// </summary>
    protected static string Format(DateTime time)
    {
        return time.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    protected static string Time(DateTime time)
    {
        return $"'{Format(time)}'";
    }

    protected static string Quote(string value)
    {
        return $"'{SqlSafety.EscapeLiteral(DatabaseProvider.ClickHouse, value)}'";
    }

    // ---------------------------------------------------------------------------------------------------
    // The catalog
    // ---------------------------------------------------------------------------------------------------

    protected static long Count(string connectionString, string from)
    {
        return Scalar<long>(connectionString, $"SELECT count() FROM {from}");
    }

    /// <summary>
    /// SHOW CREATE TABLE without the database name, which differs per test.
    /// </summary>
    protected static string ShowCreate(string connectionString, string table)
    {
        var database = new ClickHouseConnectionStringBuilder(connectionString).Database;

        return Scalar<string>(connectionString, $"SHOW CREATE TABLE {table}")
            .Replace($"CREATE TABLE {database}.", "CREATE TABLE ")
            .Replace($"CREATE MATERIALIZED VIEW {database}.", "CREATE MATERIALIZED VIEW ")
            .Replace($" {database}.", " ");
    }

    /// <summary>
    /// The tables and views of the test's database, without the hidden target tables of materialized views.
    /// </summary>
    protected static List<string> Tables(string connectionString)
    {
        return Strings(connectionString, "SELECT name FROM system.tables WHERE database = currentDatabase() AND NOT startsWith(name, '.') ORDER BY name");
    }

    protected static List<string> GrantedRoles(string connectionString, string userName)
    {
        return Strings(connectionString, $"SELECT granted_role_name FROM system.role_grants WHERE user_name = '{userName}' ORDER BY granted_role_name");
    }

    protected static List<string> RoleGrants(string connectionString, string roleName)
    {
        return Strings(connectionString, $"SELECT concat(access_type, ' ON ', table) FROM system.grants WHERE role_name = '{roleName}' ORDER BY table");
    }

    // ---------------------------------------------------------------------------------------------------
    // Accounts
    // ---------------------------------------------------------------------------------------------------

    protected static string ExporterConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "exporter"));
    }

    protected static string ReaderConnectionString(DbMigratorRequest request)
    {
        return WithUser(request.ConnectionString, request.Users.Single(user => user.Access == "ro"));
    }

    protected static string WithUser(string connectionString, DatabaseUser user)
    {
        return new ClickHouseConnectionStringBuilder(connectionString) { Username = user.User, Password = user.Password }.ToString();
    }

    protected static string WithDatabase(string connectionString, string databaseName)
    {
        return new ClickHouseConnectionStringBuilder(connectionString) { Database = databaseName }.ToString();
    }

    // ---------------------------------------------------------------------------------------------------
    // SQL
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the statement and asserts the server refused it with the given code, naming the statement if not.
    /// </summary>
    protected static void AssertError(string connectionString, string sql, int errorCode)
    {
        var exception = Record.Exception(() => Execute(connectionString, sql));

        Assert.True(exception is ClickHouseServerException server && server.ErrorCode == errorCode,
            $"Expected error {errorCode} from: {sql}{Environment.NewLine}Got: {exception?.Message ?? "no error"}");
    }

    protected static void AssertDenied(string connectionString, string sql)
    {
        AssertError(connectionString, sql, AccessDenied);
    }

    protected static void Execute(string connectionString, string sql)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    protected static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = new ClickHouseConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand(sql);

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    protected static List<string> Strings(string connectionString, string sql)
    {
        return Column(connectionString, sql, value => Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    protected static List<long> Longs(string connectionString, string sql)
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
}
