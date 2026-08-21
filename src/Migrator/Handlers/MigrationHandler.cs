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

using ClickHouse.Driver.ADO;
using DbUp.Engine;
using Microsoft.Extensions.Logging;
using Npgsql;
using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Extensions;

namespace YB.TradeServer.Db.Migrator.Handlers;

/// <summary>
/// Turns the command line - optionally enriched with the trade server export_config.json - into one or more
/// migration runs and reports the outcome of each of them.
/// </summary>
public class MigrationHandler : IMigrationHandler
{
    public const string ConnectionStringEnvironmentVariable = "TSDB_CONNECTION_STRING";

    private const string DefaultTradesTable = "trades";
    private const string DefaultCursorTable = "export_cursor";

    private const ushort DefaultClickHousePort = 8123;
    private const ushort DefaultPostgresPort = 5432;

    /// <summary>
    /// The variables that name a database object and therefore end up in DDL as text.
    /// </summary>
    private static readonly string[] identifierVariables = ["database_name", "trades_table", "cursor_table", "cluster_name"];

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
        var targets = ResolveTargets(commandLineOptions);

        foreach (var target in targets)
        {
            Migrate(target, commandLineOptions);
        }
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

    private IList<MigrationTarget> ResolveTargets(CommandLineOptions options)
    {
        var requestedProvider = string.IsNullOrWhiteSpace(options.Provider)
            ? (DatabaseProvider?)null
            : ParseProvider(options.Provider);

        if (string.IsNullOrWhiteSpace(options.ExportConfig))
        {
            if (requestedProvider == null)
            {
                throw new ArgumentException("--provider is required when --export-config is not supplied", nameof(options.Provider));
            }

            var connectionString = ResolveConnectionString(options, null, requestedProvider.Value);

            return [new(requestedProvider.Value, connectionString, DefaultTradesTable, DefaultCursorTable)];
        }

        var exportConfiguration = ExportConfiguration.Load(options.ExportConfig);

        logger.LogInformation("Loaded {PipelineCount} pipeline(s) from {ExportConfigPath}", exportConfiguration.Pipelines.Count, options.ExportConfig);

        var targets = exportConfiguration.Pipelines
            .Select(pipeline => new
            {
                Pipeline = pipeline,
                Provider = ParseProvider(pipeline.Target)
            })
            .Where(pipeline => requestedProvider == null || pipeline.Provider == requestedProvider)
            .Select(pipeline => new MigrationTarget(
                pipeline.Provider,
                ResolveConnectionString(options, pipeline.Pipeline.ExportTarget, pipeline.Provider),
                pipeline.Pipeline.ExportTarget.Table ?? DefaultTradesTable,
                pipeline.Pipeline.ExportTarget.CursorTable ?? DefaultCursorTable))
            .ToList();

        if (targets.Count == 0)
        {
            throw new SchemaMigrationException($"No pipeline in {options.ExportConfig} targets '{options.Provider}'");
        }

        return targets;
    }

    private static string ResolveConnectionString(CommandLineOptions options, ExportTarget? exportTarget, DatabaseProvider provider)
    {
        // an explicitly supplied connection string always wins, so a migration can be pointed at a different
        // host than the one the trade server writes to
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return options.ConnectionString;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (exportTarget == null)
        {
            throw new ArgumentException(
                $"A connection string is required. Supply --connection-string, set {ConnectionStringEnvironmentVariable}, or point --export-config at the trade server export configuration",
                nameof(options.ConnectionString));
        }

        return BuildConnectionString(exportTarget, provider);
    }

    private static string BuildConnectionString(ExportTarget exportTarget, DatabaseProvider provider)
    {
        var host = exportTarget.Host.ThrowWhenNullOrEmpty(nameof(exportTarget.Host), "The export configuration does not specify a host");
        var database = exportTarget.Database.ThrowWhenNullOrEmpty(nameof(exportTarget.Database), "The export configuration does not specify a database");

        switch (provider)
        {
            case DatabaseProvider.ClickHouse:
                var clickHouseBuilder = new ClickHouseConnectionStringBuilder
                {
                    Host = host,
                    Port = (ushort)(exportTarget.Port ?? DefaultClickHousePort),
                    Database = database
                };

                if (!string.IsNullOrEmpty(exportTarget.User))
                {
                    clickHouseBuilder.Username = exportTarget.User;
                }

                if (!string.IsNullOrEmpty(exportTarget.Password))
                {
                    clickHouseBuilder.Password = exportTarget.Password;
                }

                return clickHouseBuilder.ToString();

            case DatabaseProvider.Postgres:
                var postgresBuilder = new NpgsqlConnectionStringBuilder
                {
                    Host = host,
                    Port = exportTarget.Port ?? DefaultPostgresPort,
                    Database = database,
                    Username = exportTarget.User,
                    Password = exportTarget.Password
                };

                return postgresBuilder.ToString();

            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
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

    private void Migrate(MigrationTarget target, CommandLineOptions options)
    {
        var databaseName = GetDatabaseName(target.Provider, target.ConnectionString)
            .ThrowWhenNullOrEmpty("database", "The connection string does not specify a database name");

        // the journal table name is written into CREATE TABLE, INSERT and DELETE, none of which take a bound
        // parameter for an object name
        SqlSafety.ValidateIdentifier(options.JournalTable, "--journal-table");

        var clusterName = target.Provider == DatabaseProvider.ClickHouse ? options.Cluster : null;

        var users = ParseUsers(options.Users);
        var variables = BuildVariables(target, databaseName, clusterName, options);

        var scriptFolders = (options.ScriptFolders?.Any() == true
                ? options.ScriptFolders
                : [GetDefaultScriptFolder(target.Provider)])
            .Select(scriptFolder => new ScriptConfig(true, scriptFolder))
            .ToList();

        logger.LogInformation("====================================");
        logger.LogInformation(
            "Migrating {Provider} database {DatabaseName}, trades table: {TradesTable}, cursor table: {CursorTable}, users: [{Users}]",
            target.Provider,
            databaseName,
            target.TradesTable,
            target.CursorTable,
            string.Join(", ", users.Select(u => $"{u.User}({u.Access})")));

        var migrationResults = dbUpRunner.Run(new(
            target.Provider,
            target.ConnectionString,
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

    private static Dictionary<string, string> BuildVariables(MigrationTarget target, string databaseName, string? clusterName, CommandLineOptions options)
    {
        var variables = new Dictionary<string, string>
        {
            ["assembly_version"] = typeof(MigrationHandler).Assembly.GetName().Version!.ToString(),
            ["database_name"] = databaseName,
            ["trades_table"] = target.TradesTable,
            ["cursor_table"] = target.CursorTable
        };

        if (target.Provider == DatabaseProvider.ClickHouse)
        {
            var clustered = !string.IsNullOrEmpty(clusterName);

            variables["cluster_name"] = clusterName ?? "";
            variables["cluster_name_suffix"] = clustered ? $" ON CLUSTER '{clusterName}' " : "";

            // a single instance has no Keeper to coordinate replication, so the Replicated engines are only
            // used when a cluster was requested
            variables["merge_tree_engine"] = clustered ? "ReplicatedMergeTree" : "MergeTree";
            variables["replacing_merge_tree_engine"] = clustered ? "ReplicatedReplacingMergeTree" : "ReplacingMergeTree";
        }

        // command line variables are applied last so they can override any of the derived values
        foreach (var (key, value) in ParseKeyValuePairs(options.Variables))
        {
            variables[key] = value;
        }

        // these four name database objects and are written into DDL as text, so they are checked after the
        // command line has had its say rather than before
        foreach (var identifierVariable in identifierVariables)
        {
            if (variables.TryGetValue(identifierVariable, out var identifier) && identifier.Length > 0)
            {
                SqlSafety.ValidateIdentifier(identifier, $"##{identifierVariable}##");
            }
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
            var userRecord = ParseKeyValuePairs(user.Split(';', StringSplitOptions.RemoveEmptyEntries))
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

    private record MigrationTarget(DatabaseProvider Provider, string ConnectionString, string TradesTable, string CursorTable);
}
