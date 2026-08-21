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

using System.Data;
using ClickHouse.Driver.ADO;
using DbUp.Builder;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using YB.DbUp.ClickHouse;

// ReSharper disable once CheckNamespace

namespace DbUp.ClickHouse;

/// <summary>
/// Configuration extension methods for ClickHouse.
/// </summary>
public static class ClickHouseExtensions
{
    /// <summary>
    /// Creates an upgrader for ClickHouse databases.
    /// </summary>
    /// <param name="supported">Fluent helper type.</param>
    /// <param name="connectionString">ClickHouse database connection string.</param>
    /// <param name="schemaName">The database that holds the journal table.</param>
    /// <param name="schemaTableName">The name of the journal table.</param>
    /// <param name="cluster">The name of the cluster</param>
    /// <returns>
    /// A builder for a database upgrader designed for ClickHouse databases.
    /// </returns>
    public static UpgradeEngineBuilder ClickHouseDatabase(this SupportedDatabases supported, string connectionString, string schemaName, string? schemaTableName = null, string? cluster = null)
    {
        ArgumentNullException.ThrowIfNull(schemaName);

        return ClickHouseDatabase(new ClickHouseConnectionManager(connectionString), schemaName, schemaTableName ?? "schema_versions", cluster);
    }

    /// <summary>
    /// Creates an upgrader for ClickHouse databases.
    /// </summary>
    /// <param name="connectionManager">The <see cref="ClickHouseConnectionManager"/> to be used during a database upgrade.</param>
    /// <param name="schemaName">The database that holds the journal table.</param>
    /// <param name="schemaTableName">The name of the journal table.</param>
    /// <param name="cluster">The name of the cluster</param>
    /// <returns>
    /// A builder for a database upgrader designed for ClickHouse databases.
    /// </returns>
    public static UpgradeEngineBuilder ClickHouseDatabase(IConnectionManager connectionManager, string schemaName, string schemaTableName = "schema_versions", string? cluster = null)
    {
        var builder = new UpgradeEngineBuilder();
        builder.Configure(c => c.ConnectionManager = connectionManager);
        builder.Configure(c => c.ScriptExecutor = new ClickHouseScriptExecutor(() => c.ConnectionManager, () => c.Log, null, () => c.VariablesEnabled, c.ScriptPreprocessors, () => c.Journal));
        builder.Configure(c => c.Journal = new ClickHouseTableJournal(() => c.ConnectionManager, () => c.Log, schemaName, schemaTableName, cluster));
        builder.WithPreprocessor(new ClickHousePreprocessor());
        return builder;
    }

    /// <summary>
    /// Ensures that the database specified in the connection string exists.
    /// </summary>
    /// <param name="supported">Fluent helper type.</param>
    /// <param name="connectionString">The connection string.</param>
    public static void ClickHouseDatabase(this SupportedDatabasesForEnsureDatabase supported, string connectionString)
    {
        ClickHouseDatabase(supported, connectionString, new ConsoleUpgradeLog());
    }

    /// <summary>
    /// Ensures that the database specified in the connection string exists.
    /// </summary>
    /// <param name="supported">Fluent helper type.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="logger">The <see cref="IUpgradeLog"/> used to record actions.</param>
    /// <param name="timeout">Use this to set the command time out for creating a database in case you're encountering a time out in this operation.</param>
    /// <param name="cluster">The cluster to create the database on</param>
    public static void ClickHouseDatabase(
        this SupportedDatabasesForEnsureDatabase supported,
        string connectionString,
        IUpgradeLog logger,
        int timeout = -1,
        string? cluster = null)
    {
        GetClickHouseConnectionStringBuilder(connectionString, out var masterConnectionString, out var databaseName);

        using var connection = new ClickHouseConnection(masterConnectionString);
        connection.Open();

        try
        {
            if (DatabaseExists(connection, databaseName))
            {
                EnsureDatabaseIsAvailable(connection, databaseName, logger);
                return;
            }
        }
        catch (Exception e)
        {
            logger.LogInformation(@"Database not found on server with connection string in settings: {0}", e.Message);
        }

        var clusterSuffix = string.IsNullOrEmpty(cluster) ? "" : $@" ON CLUSTER '{cluster}'";
        var sqlCommandText = $@"create database if not exists {databaseName} {clusterSuffix};";

        //Create the database...
        using (var command = connection.CreateCommand(sqlCommandText))
        {
            command.CommandType = CommandType.Text;

            if (timeout >= 0)
            {
                command.CommandTimeout = timeout;
            }

            command.ExecuteNonQuery();
        }

        logger.LogInformation(@"Created database {0}", databaseName);

        EnsureDatabaseIsAvailable(connection, databaseName, logger);

        static void GetClickHouseConnectionStringBuilder(string connectionString, out string masterConnectionString, out string databaseName)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString));
            }

            var builder = new ClickHouseConnectionStringBuilder(connectionString);
            databaseName = builder.Database;

            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new InvalidOperationException("The connection string does not specify a database name.");
            }

            builder.Database = "system";
            masterConnectionString = builder.ToString();
        }

        static bool DatabaseExists(ClickHouseConnection connection, string databaseName)
        {
            // check to see if the database already exists..
            using var command = connection.CreateCommand("select name from system.databases where name = {databaseName:String};");
            command.CommandType = CommandType.Text;

            AddParameter(command, "databaseName", databaseName);

            var result = command.ExecuteScalar();
            return result is not (null or DBNull);
        }
    }

    private static void EnsureDatabaseIsAvailable(ClickHouseConnection connection, string databaseName, IUpgradeLog logger)
    {
        logger.LogInformation(@"Checking {0} is available", databaseName);

        var index = 0;
        var timeout = 1;
        const int maxNumberOfAttempts = 7;

        while (index < maxNumberOfAttempts)
        {
            index++;

            logger.LogInformation(@"Checking {0} availability, attempt: {1} of {4}, delay: {2}{3} sec", databaseName, index, timeout, index == maxNumberOfAttempts - 1 ? "(last)" : "", maxNumberOfAttempts);

            if (!IsDatabaseReadOnly(connection, databaseName))
            {
                logger.LogInformation(@"Database {0} is ready to accept commands", databaseName);
                return;
            }

            Thread.Sleep(timeout * 1_000);

            timeout *= 2;
        }

        throw new($@"Database {databaseName} is in readonly-mode");
    }

    private static bool IsDatabaseReadOnly(ClickHouseConnection connection, string databaseName)
    {
        using var command = connection.CreateCommand("select CAST(count(*) as INT) from system.replicas where database = {databaseName:String} and is_readonly = 1;");
        command.CommandType = CommandType.Text;

        AddParameter(command, "databaseName", databaseName);

        var executeScalar = command.ExecuteScalar();

        return executeScalar switch
        {
            null => false,
            DBNull => false,
            _ => Convert.ToInt32(executeScalar) > 0
        };
    }

    private static void AddParameter(IDbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
