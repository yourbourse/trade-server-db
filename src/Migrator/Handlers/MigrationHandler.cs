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

    private const string DefaultTradesTable = "trades";
    private const string DefaultCursorTable = "export_cursor";
    private const string DefaultDailyStatementsTable = "daily_statements_export";
    private const string DefaultOrderSnapshotsTable = "daily_statements_order_snapshots";
    private const string DefaultPositionSnapshotsTable = "daily_statements_position_snapshots";
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
        "cash_transfers_view"
    ];

    /// <summary>
    /// The variables that name a database object and therefore end up in DDL as text.
    /// </summary>
    /// <remarks>
    /// Every table and view variable belongs here, so the lists are kept as one - adding a table to
    /// <see cref="tableVariables"/> alone would otherwise leave its name unvalidated.
    /// </remarks>
    private static readonly string[] identifierVariables = ["database_name", "cluster_name", ..tableVariables, ..viewVariables];

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

    private static Dictionary<string, string> BuildVariables(DatabaseProvider provider, string databaseName, string? clusterName, CommandLineOptions options)
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
