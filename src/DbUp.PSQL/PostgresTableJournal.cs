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
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;


namespace YB.DbUp.PSQL;

/// <summary>
/// An implementation of the <see cref="IJournal"/> interface which tracks version numbers for a
/// PostgreSQL database using a table called schema_versions.
/// </summary>
/// <remarks>
/// The journal deliberately mirrors the ClickHouse one: same table name, same columns, same handling of
/// re-runnable scripts, so both providers behave identically.
/// </remarks>
public class PostgresTableJournal : TableJournal
{
    /// <summary>
    /// Creates a new PostgreSQL table journal.
    /// </summary>
    /// <param name="connectionManager">The PostgreSQL connection manager.</param>
    /// <param name="logger">The upgrade logger.</param>
    /// <param name="schemaName">The schema that contains the journal table.</param>
    /// <param name="tableName">The name of the journal table.</param>
    public PostgresTableJournal(Func<IConnectionManager> connectionManager, Func<IUpgradeLog> logger, string schemaName, string tableName)
        : base(connectionManager, logger, new PostgresObjectParser(), schemaName, tableName)
    {
    }

    /// <summary>
    /// Stores the journal entry for an executed script.
    /// </summary>
    /// <remarks>
    /// Re-runnable scripts carry an <c>::md5</c> suffix in their name. Before the new entry is written every
    /// previous entry for the same script - regardless of its hash - is removed, so the journal keeps exactly
    /// one row per re-runnable script and the script re-runs whenever its content changes.
    /// </remarks>
    public override void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
    {
        EnsureTableExistsAndIsLatestVersion(dbCommandFactory);

        if (script.Name.Contains("::"))
        {
            var previousScripts = script.Name[..script.Name.IndexOf("::", StringComparison.Ordinal)];

            using (var command = GetDeleteScriptCommand(dbCommandFactory, previousScripts))
            {
                command.ExecuteNonQuery();
            }
        }

        using (var command = GetInsertScriptCommand(dbCommandFactory, script))
        {
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes the journal entries of a re-runnable script, whatever hash they carry.
    /// </summary>
    /// <remarks>
    /// The prefix is a script name taken from a file on disk, so it is bound rather than pasted into the
    /// statement.
    /// </remarks>
    protected IDbCommand GetDeleteScriptCommand(Func<IDbCommand> dbCommandFactory, string scriptNamePrefix)
    {
        var command = dbCommandFactory();

        AddParameter(command, "scriptNamePrefix", scriptNamePrefix);

        command.CommandText = $@"
DELETE FROM {FqSchemaTableName} WHERE starts_with(script_name, @scriptNamePrefix);
";
        command.CommandType = CommandType.Text;
        return command;
    }

    protected override IDbCommand GetDoesTableExistCommand(Func<IDbCommand> dbCommandFactory)
    {
        var command = dbCommandFactory();

        AddParameter(command, "schemaName", SchemaTableSchema);
        AddParameter(command, "tableName", UnquotedSchemaTableName);

        command.CommandText = DoesTableExistSql();
        command.CommandType = CommandType.Text;
        return command;
    }

    protected override string DoesTableExistSql()
    {
        return @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schemaName AND table_name = @tableName) THEN 1 ELSE 0 END;
";
    }

    private static void AddParameter(IDbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <remarks>
    /// The base class supplies bare parameter names, so the Npgsql <c>@</c> prefix is added here.
    /// </remarks>
    protected override string GetInsertJournalEntrySql(string scriptName, string applied)
    {
        return @$"
INSERT INTO {FqSchemaTableName} (script_name, applied_at) VALUES (@{scriptName}, CAST(@{applied} AS TIMESTAMP WITH TIME ZONE));
";
    }

    protected override string GetJournalEntriesSql()
    {
        return @$"
SELECT script_name FROM {FqSchemaTableName} ORDER BY applied_at ASC;
";
    }

    protected override string CreateSchemaTableSql(string quotedPrimaryKeyName)
    {
        return @$"
CREATE TABLE IF NOT EXISTS {FqSchemaTableName}
(
    script_name TEXT NOT NULL,
    applied_at  TIMESTAMP WITH TIME ZONE NOT NULL,
    CONSTRAINT {quotedPrimaryKeyName} PRIMARY KEY (script_name)
);
";
    }
}
