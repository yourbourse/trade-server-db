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

using DbUp.Builder;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using YB.DbUp.PSQL;

// ReSharper disable once CheckNamespace

namespace DbUp.PSQL;

/// <summary>
/// Configuration extension methods for PostgreSQL.
/// </summary>
public static class PostgresExtensions
{
    /// <summary>
    /// Creates an upgrader for PostgreSQL databases.
    /// </summary>
    /// <param name="supported">Fluent helper type.</param>
    /// <param name="connectionString">PostgreSQL database connection string.</param>
    /// <param name="schemaName">The schema that holds the journal table.</param>
    /// <param name="schemaTableName">The name of the journal table.</param>
    /// <returns>
    /// A builder for a database upgrader designed for PostgreSQL databases.
    /// </returns>
    public static UpgradeEngineBuilder PostgresDatabase(this SupportedDatabases supported, string connectionString, string schemaName, string? schemaTableName = null)
    {
        ArgumentNullException.ThrowIfNull(schemaName);

        return PostgresDatabase(new PostgresConnectionManager(connectionString), schemaName, schemaTableName ?? "schema_versions");
    }

    /// <summary>
    /// Creates an upgrader for PostgreSQL databases.
    /// </summary>
    /// <param name="connectionManager">The <see cref="PostgresConnectionManager"/> to be used during a database upgrade.</param>
    /// <param name="schemaName">The schema that holds the journal table.</param>
    /// <param name="schemaTableName">The name of the journal table.</param>
    /// <returns>
    /// A builder for a database upgrader designed for PostgreSQL databases.
    /// </returns>
    public static UpgradeEngineBuilder PostgresDatabase(IConnectionManager connectionManager, string schemaName, string schemaTableName = "schema_versions")
    {
        var builder = new UpgradeEngineBuilder();
        builder.Configure(c => c.ConnectionManager = connectionManager);
        builder.Configure(c => c.ScriptExecutor = new PostgresScriptExecutor(() => c.ConnectionManager, () => c.Log, schemaName, () => c.VariablesEnabled, c.ScriptPreprocessors, () => c.Journal));
        builder.Configure(c => c.Journal = new PostgresTableJournal(() => c.ConnectionManager, () => c.Log, schemaName, schemaTableName));
        builder.WithPreprocessor(new PostgresPreprocessor());
        return builder;
    }

    /// <summary>
    /// Ensures that the database specified in the connection string exists.
    /// </summary>
    /// <param name="supported">Fluent helper type.</param>
    /// <param name="connectionString">The connection string.</param>
    /// <param name="logger">The <see cref="IUpgradeLog"/> used to record actions.</param>
    /// <remarks>
    /// Creating a database requires a connection to the <c>postgres</c> maintenance database, which a managed
    /// instance does not always grant to the migration user. Failures are logged and swallowed so a run against
    /// an already provisioned database still succeeds.
    /// </remarks>
    public static void PostgresDatabase(this SupportedDatabasesForEnsureDatabase supported, string connectionString, IUpgradeLog logger)
    {
        try
        {
            EnsureDatabase.For.PostgresqlDatabase(connectionString, logger);
        }
        catch (Exception e)
        {
            logger.LogWarning("Unable to verify that the database exists, assuming it has been provisioned already: {0}", e.Message);
        }
    }
}
