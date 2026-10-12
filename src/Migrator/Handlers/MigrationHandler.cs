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

using System.Text.RegularExpressions;
using ClickHouse.Driver.ADO;
using DbUp.Engine;
using Microsoft.Extensions.Logging;
using Npgsql;
using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Extensions;

namespace YB.TradeServer.Db.Migrator.Handlers;

/// <summary>
/// Turns the command line into a migration run and reports its outcome.
/// </summary>
public class MigrationHandler : IMigrationHandler
{
    public const string ConnectionStringEnvironmentVariable = "TSDB_CONNECTION_STRING";

    /// <summary>
    /// PostgreSQL truncates an identifier at this many bytes, and ClickHouse accepts at least as many.
    /// </summary>
    private const int MaxIdentifierLength = 63;

    private const string DefaultTradesTable = "trades";
    private const string DefaultCursorTable = "export_cursor";
    private const string DefaultDailyStatementsTable = "daily_statements_export";
    private const string DefaultOrderSnapshotsTable = "daily_statements_order_snapshots";
    private const string DefaultPositionSnapshotsTable = "daily_statements_position_snapshots";
    private const string DefaultTradingAccountEventsTable = "trading_account_events";
    private const string DefaultTradingAccountsTable = "trading_accounts";
    private const string DefaultTradingAccountsResyncView = "trading_accounts_resync";
    private const string DefaultClientEventsTable = "client_events";
    private const string DefaultClientsTable = "clients";
    private const string DefaultClientsResyncView = "clients_resync";
    private const string DefaultSymbolEventsTable = "symbol_events";
    private const string DefaultSymbolsTable = "symbols";
    private const string DefaultSymbolsResyncView = "symbols_resync";
    private const string DefaultGroupEventsTable = "group_events";
    private const string DefaultGroupsTable = "groups";
    private const string DefaultGroupsResyncView = "groups_resync";
    private const string DefaultTicksTable = "ticks";
    private const string DefaultTicksTtlDays = "31";
    private const string DefaultTradesHistoryView = "trades_history";
    private const string DefaultCashTransfersView = "cash_transfers";

    /// <summary>
    /// The variables naming a table the trade server writes to, in the order the migration log reports them.
    /// </summary>
    private static readonly string[] tableVariables =
    [
        "trades_table",
        "daily_statements_table",
        "order_snapshots_table",
        "position_snapshots_table",
        "trading_account_events_table",
        "client_events_table",
        "symbol_events_table",
        "group_events_table",
        "cursor_table",
        "ticks_table"
    ];

    /// <summary>
    /// The variables naming a view this repository creates over the exported tables.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="tableVariables"/> because the trade server never writes to a view and so has
    /// no say in its name. They are templated all the same, so a deployment that already owns an object under
    /// one of the default names can move ours aside with --variables.
    /// </remarks>
    private static readonly string[] viewVariables =
    [
        "trades_history_view",
        "cash_transfers_view",
        "trading_accounts_resync_view",
        "clients_resync_view"
        "symbols_resync_view",
        "groups_resync_view"
    ];

    /// <summary>
    /// The variables naming a table this repository derives from an exported table and maintains itself.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="tableVariables"/> for the same reason <see cref="viewVariables"/> is: the
    /// trade server never writes to one of these, and so has no say in its name. They are templated all the
    /// same, so a deployment that already owns an object under one of the default names can move ours aside
    /// with --variables.
    /// </remarks>
    private static readonly string[] derivedTableVariables =
    [
        "trading_accounts_table",
        "clients_table"
        "symbols_table",
        "groups_table"
    ];

    /// <summary>
    /// The variables that name a database object and therefore end up in DDL as text.
    /// </summary>
    /// <remarks>
    /// Every table, view and derived table variable belongs here, so the lists are kept as one - adding a table
    /// to <see cref="tableVariables"/> alone would otherwise leave its name unvalidated.
    /// </remarks>
    private static readonly string[] identifierVariables = ["database_name", "cluster_name", ..tableVariables, ..viewVariables, ..derivedTableVariables];

    /// <summary>
    /// The longest prefix the scripts attach to each table name when they derive another object's name from it.
    /// </summary>
    /// <remarks>
    /// PostgreSQL truncates an identifier at 63 bytes, silently. The scripts build index, function and trigger
    /// names by prefixing a configured table name - <c>idx_markers_</c>, <c>project_</c>, <c>trg_project_</c> -
    /// so a name long enough to push the result past 63 is cut, and two different configured names that agree
    /// up to the cut produce one object. For an index that means the second CREATE ... IF NOT EXISTS quietly
    /// does nothing; for the projection function it is worse, because the second CREATE OR REPLACE FUNCTION
    /// overwrites the first and one of the two triggers then runs the other entity's body against its own NEW
    /// record.
    ///
    /// Checking the derived length here turns all of that into a message before any DDL is built. The values
    /// are the longest prefix in the scripts for each variable, so they have to be revisited if a longer one is
    /// introduced - a test asserts every templated table name is covered.
    /// </remarks>
    private static readonly Dictionary<string, (int Prefix, string Example)> derivedNamePrefixes = new()
    {
        ["trades_table"] = (27, "idx_transfers_account_time_"),
        ["daily_statements_table"] = (24, "idx_sender_stmt_account_"),
        ["order_snapshots_table"] = (22, "idx_sender_stmt_order_"),
        ["position_snapshots_table"] = (25, "idx_sender_stmt_position_"),
        ["trading_account_events_table"] = (16, "idx_account_row_"),
        ["trading_accounts_table"] = (12, "trg_project_"),
        ["client_events_table"] = (15, "idx_client_row_"),
        ["clients_table"] = (12, "trg_project_"),
        ["group_events_table"] = (14, "idx_group_row_"),
        ["groups_table"] = (12, "trg_project_"),
        ["cursor_table"] = (0, ""),
        ["ticks_table"] = (0, "")
    };

    /// <summary>
    /// Splits a user definition only at the semicolons that introduce one of its keys.
    /// </summary>
    /// <remarks>
    /// A password is free-form and may itself contain a semicolon, so splitting on every semicolon would
    /// silently truncate it: <c>Password=pa;ss</c> has to store <c>pa;ss</c>, not <c>pa</c>. The one sequence a
    /// password still cannot contain is a semicolon immediately followed by one of the keys.
    /// </remarks>
    private static readonly Regex userDefinitionSeparator = new(@";(?=\s*(?:Access|User|Password)\s*=)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IDbUpRunner dbUpRunner;
    private readonly ILogger<MigrationHandler> logger;

    public MigrationHandler(
        IDbUpRunner dbUpRunner,
        ILogger<MigrationHandler> logger)
    {
        this.dbUpRunner = dbUpRunner;
        this.logger = logger;
    }

    public void Handle(CommandLineOptions commandLineOptions)
    {
        var provider = ParseProvider(commandLineOptions.Provider
            .ThrowWhenNullOrEmpty(nameof(commandLineOptions.Provider), "--provider is required"));

        Migrate(provider, ResolveConnectionString(commandLineOptions), commandLineOptions);
    }

    public static DatabaseProvider ParseProvider(string provider)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "clickhouse" or "ch" => DatabaseProvider.ClickHouse,
            "postgres" or "postgresql" or "psql" or "pg" => DatabaseProvider.Postgres,
            _ => throw new ArgumentException($"Unknown provider '{provider}'. Supported values: 'clickhouse', 'postgres'", nameof(provider))
        };
    }

    /// <summary>
    /// The folder holding the scripts of a provider, relative to the root of the repository.
    /// </summary>
    public static string GetDefaultScriptFolder(DatabaseProvider provider)
    {
        return provider switch
        {
            DatabaseProvider.ClickHouse => "clickhouse",
            DatabaseProvider.Postgres => "psql",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
    }

    private static string ResolveConnectionString(CommandLineOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return options.ConnectionString;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        throw new ArgumentException(
            $"A connection string is required. Supply --connection-string or set {ConnectionStringEnvironmentVariable}",
            nameof(options.ConnectionString));
    }

    private static string GetDatabaseName(DatabaseProvider provider, string connectionString)
    {
        return provider switch
        {
            DatabaseProvider.ClickHouse => new ClickHouseConnectionStringBuilder(connectionString).Database,
            DatabaseProvider.Postgres => new NpgsqlConnectionStringBuilder(connectionString).Database ?? "",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
    }

    private void Migrate(DatabaseProvider provider, string connectionString, CommandLineOptions options)
    {
        var databaseName = GetDatabaseName(provider, connectionString)
            .ThrowWhenNullOrEmpty("database", "The connection string does not specify a database name");

        // the journal table name is written into CREATE TABLE, INSERT and DELETE, none of which take a bound
        // parameter for an object name
        SqlSafety.ValidateIdentifier(options.JournalTable, "--journal-table");

        var clusterName = provider == DatabaseProvider.ClickHouse ? options.Cluster : null;

        var users = ParseUsers(options.Users);
        var variables = BuildVariables(provider, databaseName, clusterName, options);

        var scriptFolders = (options.ScriptFolders?.Any() == true
                ? options.ScriptFolders
                : [GetDefaultScriptFolder(provider)])
            .Select(scriptFolder => new ScriptConfig(true, scriptFolder))
            .ToList();

        logger.LogInformation("====================================");
        logger.LogInformation(
            "Migrating {Provider} database {DatabaseName}, tables: [{Tables}], users: [{Users}]",
            provider,
            databaseName,
            string.Join(", ", tableVariables.Select(variable => $"{variable}={variables[variable]}")),
            string.Join(", ", users.Select(u => $"{u.User}({u.Access})")));

        var migrationResults = dbUpRunner.Run(new(
            provider,
            connectionString,
            databaseName,
            options.JournalTable,
            scriptFolders,
            options.ScriptsRoot,
            variables,
            options.FullReload,
            options.DryRun,
            clusterName,
            users));

        foreach (var (subFolder, migrationResult) in migrationResults)
        {
            DisplayMigrationResult(migrationResult, subFolder);
        }
    }

    /// <summary>
    /// The template variables a run substitutes into the scripts: the derived values, then anything --variables
    /// adds or overrides, with every name that reaches DDL validated as an identifier afterwards.
    /// </summary>
    /// <remarks>
    /// Public so the tests can assert the full set. A script naming a variable this does not produce is rejected
    /// before it reaches the server, so adding a variable here without adding it everywhere a variable set is
    /// assembled - the integration fixture included - fails every migration that touches the script using it.
    /// </remarks>
    public static Dictionary<string, string> BuildVariables(DatabaseProvider provider, string databaseName, string? clusterName, CommandLineOptions options)
    {
        var variables = new Dictionary<string, string>
        {
            ["assembly_version"] = typeof(MigrationHandler).Assembly.GetName().Version!.ToString(),
            ["database_name"] = databaseName,
            ["trades_table"] = DefaultTradesTable,
            ["cursor_table"] = DefaultCursorTable,
            ["daily_statements_table"] = DefaultDailyStatementsTable,
            ["order_snapshots_table"] = DefaultOrderSnapshotsTable,
            ["position_snapshots_table"] = DefaultPositionSnapshotsTable,
            ["trading_account_events_table"] = DefaultTradingAccountEventsTable,
            ["trading_accounts_table"] = DefaultTradingAccountsTable,
            ["trading_accounts_resync_view"] = DefaultTradingAccountsResyncView,
            ["client_events_table"] = DefaultClientEventsTable,
            ["clients_table"] = DefaultClientsTable,
            ["clients_resync_view"] = DefaultClientsResyncView,
            ["symbol_events_table"] = DefaultSymbolEventsTable,
            ["symbols_table"] = DefaultSymbolsTable,
            ["symbols_resync_view"] = DefaultSymbolsResyncView,
            ["group_events_table"] = DefaultGroupEventsTable,
            ["groups_table"] = DefaultGroupsTable,
            ["groups_resync_view"] = DefaultGroupsResyncView,
            ["ticks_table"] = DefaultTicksTable,
            ["trades_history_view"] = DefaultTradesHistoryView,
            ["cash_transfers_view"] = DefaultCashTransfersView
        };

        if (provider == DatabaseProvider.ClickHouse)
        {
            var clustered = !string.IsNullOrEmpty(clusterName);

            variables["cluster_name"] = clusterName ?? "";
            variables["cluster_name_suffix"] = clustered ? $" ON CLUSTER '{clusterName}' " : "";

            // a single instance has no Keeper to coordinate replication, so the Replicated engines are only
            // used when a cluster was requested
            variables["merge_tree_engine"] = clustered ? "ReplicatedMergeTree" : "MergeTree";
            variables["replacing_merge_tree_engine"] = clustered ? "ReplicatedReplacingMergeTree" : "ReplacingMergeTree";

            variables["ticks_ttl_days"] = DefaultTicksTtlDays;
        }

        // command line variables are applied last so they can override any of the derived values
        foreach (var (key, value) in ParseKeyValuePairs(options.Variables))
        {
            variables[key] = value;
        }

        // these name database objects and are written into DDL as text, so they are checked after the command
        // line has had its say rather than before
        foreach (var identifierVariable in identifierVariables)
        {
            if (variables.TryGetValue(identifierVariable, out var identifier) && identifier.Length > 0)
            {
                SqlSafety.ValidateIdentifier(identifier, $"##{identifierVariable}##");
            }
        }

        // a valid 63 character name is still too long once the scripts prefix it to derive an index, a function
        // or a trigger, and PostgreSQL truncates the result rather than complaining
        foreach (var (variable, (prefix, example)) in derivedNamePrefixes)
        {
            if (prefix > 0 && variables.TryGetValue(variable, out var name) && name.Length + prefix > MaxIdentifierLength)
            {
                throw new SchemaMigrationException(
                    $"##{variable}## '{name}' is {name.Length} characters, which leaves no room for the names " +
                    $"derived from it. The scripts prefix it with up to {prefix} characters (for example " +
                    $"'{example}'), and PostgreSQL truncates an identifier at {MaxIdentifierLength} bytes - so " +
                    $"the derived object could collide with another one. Use a name of at most " +
                    $"{MaxIdentifierLength - prefix} characters.");
            }
        }

        // the retention is written into MODIFY TTL as text, so it gets the same treatment as an identifier
        if (variables.TryGetValue("ticks_ttl_days", out var ticksTtlDays))
        {
            SqlSafety.ValidatePositiveInteger(ticksTtlDays, "##ticks_ttl_days##");
        }

        return variables;
    }

    /// <summary>
    /// Expected format: Access=exporter;User=trade_exporter;Password=passw0rd
    /// </summary>
    public static IList<DatabaseUser> ParseUsers(IEnumerable<string>? users)
    {
        var parsedUsers = new List<DatabaseUser>();

        if (users == null)
        {
            return parsedUsers;
        }

        foreach (var user in users)
        {
            var userRecord = ParseKeyValuePairs(userDefinitionSeparator.Split(user))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            if (!userRecord.TryGetValue("Access", out var access) ||
                !userRecord.TryGetValue("User", out var userName) ||
                !userRecord.TryGetValue("Password", out var password))
            {
                throw new ArgumentException($"Unable to parse the user definition '{user.MaskSecrets("Password")}'. Expected format: Access=exporter;User=trade_exporter;Password=passw0rd", nameof(users));
            }

            parsedUsers.Add(new()
            {
                Access = access,
                User = userName,
                Password = password
            });
        }

        return parsedUsers;
    }

    /// <summary>
    /// Expected format: key=value
    /// </summary>
    public static IList<KeyValuePair<string, string>> ParseKeyValuePairs(IEnumerable<string>? pairs)
    {
        if (pairs == null)
        {
            return [];
        }

        return pairs
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new KeyValuePair<string, string>(parts[0].Trim(), parts[1]))
            .ToList();
    }

    private void DisplayMigrationResult(DatabaseUpgradeResult migrationResult, string scriptPath)
    {
        logger.LogInformation("====================================");
        logger.LogInformation("Scripts path: {ScriptPath}", scriptPath);
        logger.LogInformation("Executed scripts: {NumberOfMigrations}", migrationResult.Scripts.Count());

        foreach (var resultScript in migrationResult.Scripts)
        {
            logger.LogInformation(" - {Script}", resultScript.Name);
        }

        if (!migrationResult.Successful)
        {
            logger.LogInformation("Exception: {Error}", migrationResult.Error);
            logger.LogInformation("ErrorScript: {ScriptName} - {ScriptContent}", migrationResult.ErrorScript?.Name, migrationResult.ErrorScript?.Contents);

            throw new SchemaMigrationException($"Unable to upgrade the database. An exception occured in script: `{migrationResult.ErrorScript?.Name}`", migrationResult.Error);
        }
    }
}
