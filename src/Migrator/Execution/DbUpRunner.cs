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

using System.Reflection;
using System.Text.RegularExpressions;
using DbUp;
using DbUp.Builder;
using DbUp.ClickHouse;
using DbUp.Engine;
using DbUp.Helpers;
using DbUp.PSQL;
using Microsoft.Extensions.Logging;
using YB.DbUp.ClickHouse;
using YB.DbUp.PSQL;
using YB.TradeServer.Db.Migrator.Extensions;
using YB.TradeServer.Db.Migrator.Logging;

namespace YB.TradeServer.Db.Migrator.Execution;

/// <summary>
/// Walks the script folders in phase order and hands every phase to DbUp.
/// </summary>
/// <remarks>
/// The folder walk, the de-duplication and the journal naming are provider independent - only the upgrade
/// engine and the "create the database if it is missing" step differ between ClickHouse and PostgreSQL.
/// </remarks>
public class DbUpRunner : IDbUpRunner
{
    /// <summary>
    /// Long enough for a mutation over a large table to complete without the client giving up.
    /// </summary>
    private static readonly TimeSpan executionTimeout = TimeSpan.FromDays(1);

    private static readonly Regex unresolvedVariablePattern = new(@"##[A-Za-z0-9_]+##", RegexOptions.Compiled);

    private static readonly List<ExecutionOrder> scriptExecutionOrder =
    [
        new() { OrderId = 1, SubFolder = "pre", ReRunnable = true, FileContentResolver = DefaultFileTransformation },
        new() { OrderId = 2, SubFolder = null, ReRunnable = false, FileContentResolver = DefaultFileTransformation },
        new() { OrderId = 3, SubFolder = "views", ReRunnable = true, FileContentResolver = DefaultFileTransformation },
        new() { OrderId = 4, SubFolder = "users", ReRunnable = true, FileContentResolver = UserFileTransformationResolver },
        new() { OrderId = 5, SubFolder = "post", ReRunnable = true, FileContentResolver = DefaultFileTransformation }
    ];

    private readonly ILogger<DbUpRunner> logger;
    private readonly IDbUpLogProvider logProvider;

    public DbUpRunner(
        ILogger<DbUpRunner> logger,
        IDbUpLogProvider logProvider)
    {
        this.logger = logger;
        this.logProvider = logProvider;
    }

    public List<(string SubFolder, DatabaseUpgradeResult Result)> Run(DbMigratorRequest dbMigrationRequest)
    {
        var currentFolder = dbMigrationRequest.ScriptsRoot ?? Directory.GetCurrentDirectory();
        var assemblyEntryDirectory = GetAssemblyEntryDirectory();

        logger.LogInformation("====================================");
        logger.LogInformation("Checking script folders: [{ScriptFolders}]", string.Join(", ", dbMigrationRequest.ScriptFolders.Select(s => $"{s.ScriptFolder}{(!s.IsRequired ? "(optional)" : "")}")));

        var filesToExecute = new List<ExecutionStep>();

        foreach (var executionOrder in scriptExecutionOrder)
        {
            var fileOrders = new List<FileOrder>();
            var order = 0;

            foreach (var (isRequired, scriptFolder) in dbMigrationRequest.ScriptFolders)
            {
                order++;

                var currentPath = Path.Combine(currentFolder, scriptFolder);
                var executablePath = Path.Combine(assemblyEntryDirectory, scriptFolder);

                if (!Directory.Exists(currentPath) && !Directory.Exists(executablePath))
                {
                    if (isRequired)
                    {
                        throw new SchemaMigrationException($"Script folder doesn't exist: {scriptFolder}");
                    }

                    // an optional folder that is not present is simply skipped
                    continue;
                }

                var scriptRootFolder = Directory.Exists(currentPath) ? currentPath : executablePath;

                var path = Path.Combine(scriptRootFolder, executionOrder.SubFolder ?? "");

                if (Directory.Exists(path))
                {
                    fileOrders.AddRange(Directory.GetFiles(path, "*.sql").Select(s => new FileOrder(s, order)));
                }
            }

            var deduplicatedFileOrders = DeduplicateFiles(fileOrders);

            filesToExecute.Add(new(executionOrder, deduplicatedFileOrders));
        }

        var executionResults = new List<(string, DatabaseUpgradeResult)>();

        if (dbMigrationRequest.DryRun)
        {
            logger.LogInformation("Dry run: the database will not be created and no script will be executed");
        }
        else
        {
            EnsureDatabaseExists(dbMigrationRequest);
        }

        var templateVariables = dbMigrationRequest.Variables
            .ToDictionary(s => $"##{s.Key}##", s => s.Value);

        foreach (var executionStep in filesToExecute.OrderBy(x => x.ExecutionOrder.OrderId))
        {
            if (!executionStep.Files.Any())
            {
                logger.LogInformation("No files for {SubFolder}", executionStep.ExecutionOrder.SubFolder ?? "(root)");
                continue;
            }

            logger.LogInformation("====================================");
            logger.LogInformation("Running migration: {SubFolder}", executionStep.ExecutionOrder.SubFolder ?? "(root)");

            var sqlScripts = executionStep.Files
                .SelectMany(scriptOrder => executionStep.ExecutionOrder.FileContentResolver(executionStep, scriptOrder, templateVariables, dbMigrationRequest));

            var upgradeResult = ExecuteScriptsInFolder(dbMigrationRequest, sqlScripts, executionStep.ExecutionOrder);
            executionResults.Add((executionStep.ExecutionOrder.SubFolder ?? "(root)", upgradeResult));

            if (!upgradeResult.Successful)
            {
                break;
            }
        }

        return executionResults;
    }

    /// <summary>
    /// When a file exists in multiple folders, it's assumed the user wants to override the file for a specific
    /// deployment. The code below will keep the file from the last folder.
    /// </summary>
    /// <remarks>
    /// Example:
    /// * psql/post/script1.sql
    /// * psql/post/script2.sql
    /// * custom/post/script1.sql
    ///
    /// The code will return
    /// * psql/post/script2.sql
    /// * custom/post/script1.sql
    /// </remarks>
    public static IList<FileOrder> DeduplicateFiles(IEnumerable<FileOrder> fileOrders)
    {
        var result = new List<FileOrder>();

        // when file names are the same, we want to keep the one from the last folder
        foreach (var orderedGroup in fileOrders
                     .GroupBy(r => $"{new FileInfo(r.FilePath).Directory?.Name}/{new FileInfo(r.FilePath).Name}"))
        {
            var filePath = orderedGroup
                .OrderByDescending(f => f.Order)
                .First();

            result.Add(filePath);
        }

        // sort by order and then by file path
        return result
            .OrderBy(r => r.Order)
            .ThenBy(r => r.FilePath)
            .ToList();
    }

    private void EnsureDatabaseExists(DbMigratorRequest request)
    {
        switch (request.Provider)
        {
            case DatabaseProvider.ClickHouse:
                EnsureDatabase.For.ClickHouseDatabase(request.ConnectionString, logProvider, cluster: request.ClusterName);
                break;

            case DatabaseProvider.Postgres:
                EnsureDatabase.For.PostgresDatabase(request.ConnectionString, logProvider);
                break;

            default:
                throw new SchemaMigrationException($"Unsupported provider: {request.Provider}");
        }
    }

    private UpgradeEngineBuilder CreateUpgradeEngineBuilder(DbMigratorRequest request)
    {
        return request.Provider switch
        {
            DatabaseProvider.ClickHouse => DeployChanges.To.ClickHouseDatabase(request.ConnectionString, request.DatabaseName, request.JournalTable, request.ClusterName),
            DatabaseProvider.Postgres => DeployChanges.To.PostgresDatabase(request.ConnectionString, "public", request.JournalTable),
            _ => throw new SchemaMigrationException($"Unsupported provider: {request.Provider}")
        };
    }

    private DatabaseUpgradeResult ExecuteScriptsInFolder(DbMigratorRequest dbMigratorRequest, IEnumerable<SqlScript> sqlScripts, ExecutionOrder executionRule)
    {
        var scripts = sqlScripts.ToList();

        var databaseUpdater = CreateUpgradeEngineBuilder(dbMigratorRequest)
            .WithScripts(scripts)
            .LogTo(logProvider)
            .WithExecutionTimeout(executionTimeout);

        if (dbMigratorRequest.Provider == DatabaseProvider.Postgres)
        {
            // PostgreSQL DDL is transactional, so a failing script leaves nothing half applied.
            // ClickHouse has no equivalent, which is why the transaction is only configured here.
            databaseUpdater.WithTransactionPerScript();
        }

        if (executionRule.ReRunnable)
        {
            // ALL views, users and post scripts will be re-applied
            if (dbMigratorRequest.FullReload)
            {
                databaseUpdater.JournalTo(new NullJournal());
            }

            // Nothing to do otherwise: the journal configured by the provider already deletes the previous
            // entry for a re-runnable script, so the script re-runs whenever its MD5 hash changes.
        }

        var deployment = databaseUpdater.Build();

        if (dbMigratorRequest.DryRun)
        {
            List<SqlScript> pendingScripts;

            try
            {
                pendingScripts = deployment.GetScriptsToExecute();
            }
            catch (Exception e)
            {
                // reading the journal fails when the database has not been provisioned yet, and a dry run is
                // not allowed to create it - so every script is reported as pending instead
                logger.LogWarning("Dry run: unable to read the journal ({ErrorMessage}). Reporting every script as pending", e.Message);
                pendingScripts = scripts;
            }

            logger.LogInformation("Dry run: {PendingScriptCount} script(s) would be applied for {SubFolder}", pendingScripts.Count, executionRule.SubFolder ?? "(root)");

            foreach (var pendingScript in pendingScripts)
            {
                logger.LogInformation(" - {Script}", pendingScript.Name);
            }

            return new(pendingScripts, true, null, null);
        }

        return deployment.PerformUpgrade();
    }

    private static (string result, string hash) SubstituteTemplateVariables(string filePath, IDictionary<string, string> variables)
    {
        var content = File.ReadAllText(filePath);

        var result = content;

        foreach (var (templateKey, replacementValue) in variables)
        {
            result = result.Replace(templateKey, replacementValue);
        }

        var unresolvedVariables = FindUnresolvedVariables(result);

        if (unresolvedVariables.Count > 0)
        {
            throw new SchemaMigrationException(
                $"{Path.GetFileName(filePath)} uses {string.Join(", ", unresolvedVariables)}, which the migrator does not know about. " +
                $"Known variables: {string.Join(", ", variables.Keys.Order())}. " +
                "Pass any other value with --variables name=value.");
        }

        return (result, result.GetHash());
    }

    /// <summary>
    /// Finds the ##name## placeholders left behind after substitution.
    /// </summary>
    /// <remarks>
    /// An unknown placeholder reaches the server verbatim and comes back as an unhelpful syntax error pointing
    /// at a character offset, so the script is rejected here instead, naming the variable that is missing.
    /// </remarks>
    public static IList<string> FindUnresolvedVariables(string content)
    {
        return unresolvedVariablePattern.Matches(content)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<SqlScript> DefaultFileTransformation(ExecutionStep executionStep, FileOrder scriptOrder, Dictionary<string, string> templateVariables, DbMigratorRequest dbMigrationRequest)
    {
        var filePath = scriptOrder.FilePath;
        var scriptFolder = ResolveScriptFolder(filePath, dbMigrationRequest);

        // the name carries the sort order of the batch. If the same file name exists in two folders we want to
        // run the one from the first folder first
        var fileUniqueName = $"[{scriptFolder}{(string.IsNullOrEmpty(executionStep.ExecutionOrder.SubFolder) ? "" : $"/{executionStep.ExecutionOrder.SubFolder}")}] {Path.GetFileName(filePath)}";
        var (contents, md5Hash) = SubstituteTemplateVariables(filePath, templateVariables);

        // append the md5 hashcode to distinguish between incremental scripts and re-runnable scripts
        if (executionStep.ExecutionOrder.ReRunnable)
        {
            fileUniqueName = $"{fileUniqueName}::{md5Hash}";
        }

        return
        [
            new SqlScript(
                fileUniqueName,
                contents,
                new() { RunGroupOrder = scriptOrder.Order })
        ];
    }

    private static IEnumerable<SqlScript> UserFileTransformationResolver(ExecutionStep executionStep, FileOrder scriptOrder, Dictionary<string, string> templateVariables, DbMigratorRequest dbMigrationRequest)
    {
        var filePath = scriptOrder.FilePath;
        var scriptFolder = ResolveScriptFolder(filePath, dbMigrationRequest);

        var accessLevel = Path.GetFileNameWithoutExtension(filePath);

        var usersWithAccessLevel = dbMigrationRequest.Users.Where(u => u.Access == accessLevel);

        foreach (var user in usersWithAccessLevel)
        {
            // the name carries the sort order of the batch. If the same file name exists in two folders we want
            // to run the one from the first folder first
            var fileUniqueName = $"[{scriptFolder}/{executionStep.ExecutionOrder.SubFolder}] {Path.GetFileName(filePath)}";
            fileUniqueName = $"{fileUniqueName} for {user.User}";

            var variables = new Dictionary<string, string>(templateVariables)
            {
                // the name lands in CREATE ROLE and GRANT, where no database accepts a bound parameter, so it
                // has to be a plain identifier; the password sits inside a quoted literal in the script and is
                // escaped for the dialect it is written into
                ["##user_name##"] = SqlSafety.ValidateIdentifier(user.User, "User name"),
                ["##user_password##"] = SqlSafety.EscapeLiteral(dbMigrationRequest.Provider, user.Password)
            };

            var (contents, md5Hash) = SubstituteTemplateVariables(filePath, variables);

            if (executionStep.ExecutionOrder.ReRunnable)
            {
                fileUniqueName = $"{fileUniqueName}::{md5Hash}";
            }

            yield return new(
                fileUniqueName,
                contents,
                new() { RunGroupOrder = scriptOrder.Order });
        }
    }

    /// <summary>
    /// Finds the configured script folder a file was picked up from. The most specific match wins, so a nested
    /// override folder is not mistaken for the base folder it sits under.
    /// </summary>
    private static string ResolveScriptFolder(string filePath, DbMigratorRequest dbMigrationRequest)
    {
        var normalisedFilePath = filePath.Replace('\\', '/');

        var scriptConfig = dbMigrationRequest.ScriptFolders
            .OrderByDescending(config => config.ScriptFolder.Length)
            .FirstOrDefault(config => normalisedFilePath.Contains(config.ScriptFolder.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        return scriptConfig?.ScriptFolder ?? Path.GetFileName(Path.GetDirectoryName(normalisedFilePath)) ?? "";
    }

    private string GetAssemblyEntryDirectory()
    {
        var codeBase = string.Empty;

        try
        {
            codeBase = Assembly.GetEntryAssembly()!.Location;
            var uri = new UriBuilder(codeBase);
            var path = Uri.UnescapeDataString(uri.Path);
            return Path.GetDirectoryName(path)!;
        }
        catch (Exception e)
        {
            logger.LogWarning("Unable to get assembly entry directory {Location}, error: {ErrorMessage}", codeBase, e.Message);
            return string.Empty;
        }
    }
}
