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

// the implementation is taken from https://github.com/DbUp/DbUp/blob/b134a35eefaa349aa7c4b00c8141e46750b927c9/src/dbup-core/Support/TableJournal.cs
// GetExecutedScripts is unchanged; DoesTableExist is routed through an overridable command factory so the
// providers can bind the schema and table name instead of pasting them into the statement.

/// <summary>
/// The base class for Journal implementations that use a table.
/// </summary>
public abstract class TableJournal : IJournal
{
    private readonly ISqlObjectParser sqlObjectParser;
    private bool journalExists;

    /// <summary>
    /// Initializes a new instance of the <see cref="TableJournal"/> class.
    /// </summary>
    /// <param name="connectionManager">The connection manager.</param>
    /// <param name="logger">The log.</param>
    /// <param name="sqlObjectParser"></param>
    /// <param name="schema">The schema that contains the table.</param>
    /// <param name="table">The table name.</param>
    protected TableJournal(
        Func<IConnectionManager> connectionManager,
        Func<IUpgradeLog> logger,
        ISqlObjectParser sqlObjectParser,
        string schema, string table)
    {
        this.sqlObjectParser = sqlObjectParser;
        ConnectionManager = connectionManager;
        Log = logger;
        UnquotedSchemaTableName = table;
        SchemaTableSchema = schema;
        FqSchemaTableName = string.IsNullOrEmpty(schema)
            ? sqlObjectParser.QuoteIdentifier(table)
            : sqlObjectParser.QuoteIdentifier(schema) + "." + sqlObjectParser.QuoteIdentifier(table);
    }

    protected string SchemaTableSchema { get; }

    /// <summary>
    /// Schema table name, no schema and unquoted
    /// </summary>
    protected string UnquotedSchemaTableName { get; }

    /// <summary>
    /// Fully qualified schema table name, includes schema and is quoted.
    /// </summary>
    protected string FqSchemaTableName { get; }

    protected Func<IConnectionManager> ConnectionManager { get; }

    protected Func<IUpgradeLog> Log { get; }

    public string[] GetExecutedScripts()
    {
        return ConnectionManager().ExecuteCommandsWithManagedConnection(dbCommandFactory =>
        {
            if (!journalExists && !DoesTableExist(dbCommandFactory))
            {
                Log().LogInformation("Journal table does not exist");
                return [];
            }

            Log().LogInformation("Fetching list of already executed scripts.");

            var scripts = new List<string>();

            using (var command = GetJournalEntriesCommand(dbCommandFactory))
            {
                using (var reader = command.ExecuteReader())
                {
                    do
                    {
                        while (reader.Read())
                        {
                            scripts.Add((string)reader[0]);
                        }
                    }
                    // Kept in step with the ClickHouse copy of this file:
                    // a provider may return more than one result set, so every one of them is drained
                    while (reader.NextResult());
                }
            }

            return scripts.ToArray();
        });
    }

    /// <summary>
    /// Records a database upgrade for a database specified in a given connection string.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <param name="dbCommandFactory"></param>
    public virtual void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
    {
        EnsureTableExistsAndIsLatestVersion(dbCommandFactory);

        using (var command = GetInsertScriptCommand(dbCommandFactory, script))
        {
            command.ExecuteNonQuery();
        }
    }

    protected IDbCommand GetInsertScriptCommand(Func<IDbCommand> dbCommandFactory, SqlScript script)
    {
        var command = dbCommandFactory();

        var scriptNameParam = command.CreateParameter();
        scriptNameParam.ParameterName = "scriptName";
        scriptNameParam.Value = script.Name;
        command.Parameters.Add(scriptNameParam);

        var appliedParam = command.CreateParameter();
        appliedParam.ParameterName = "applied";
        appliedParam.Value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
        command.Parameters.Add(appliedParam);

        command.CommandText = GetInsertJournalEntrySql("scriptName", "applied");
        command.CommandType = CommandType.Text;
        return command;
    }

    protected IDbCommand GetJournalEntriesCommand(Func<IDbCommand> dbCommandFactory)
    {
        var command = dbCommandFactory();
        command.CommandText = GetJournalEntriesSql();
        command.CommandType = CommandType.Text;
        return command;
    }

    protected IDbCommand GetCreateTableCommand(Func<IDbCommand> dbCommandFactory)
    {
        var command = dbCommandFactory();
        var primaryKeyName = sqlObjectParser.QuoteIdentifier("PK_" + UnquotedSchemaTableName + "_Id");
        command.CommandText = CreateSchemaTableSql(primaryKeyName);
        command.CommandType = CommandType.Text;
        return command;
    }

    /// <summary>
    /// Sql for inserting a journal entry
    /// </summary>
    /// <param name="scriptName">Name of the script name parameter, without any provider specific prefix</param>
    /// <param name="applied">Name of the applied parameter, without any provider specific prefix</param>
    protected abstract string GetInsertJournalEntrySql(string scriptName, string applied);

    /// <summary>
    /// Sql for getting the journal entries
    /// </summary>
    protected abstract string GetJournalEntriesSql();

    /// <summary>
    /// Sql for creating journal table
    /// </summary>
    /// <param name="quotedPrimaryKeyName">Following PK_{TableName}_Id naming</param>
    protected abstract string CreateSchemaTableSql(string quotedPrimaryKeyName);

    /// <summary>
    /// Unquotes a quoted identifier.
    /// </summary>
    /// <param name="quotedIdentifier">identifier to unquote.</param>
    protected string UnquoteSqlObjectName(string quotedIdentifier)
    {
        return sqlObjectParser.UnquoteIdentifier(quotedIdentifier);
    }

    protected virtual void OnTableCreated(Func<IDbCommand> dbCommandFactory)
    {
    }

    public virtual void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
    {
        if (!journalExists && !DoesTableExist(dbCommandFactory))
        {
            Log().LogInformation("Creating the {0} table", FqSchemaTableName);

            // We will never change the schema of the initial table create.
            using (var command = GetCreateTableCommand(dbCommandFactory))
            {
                command.ExecuteNonQuery();
            }

            Log().LogInformation("The {0} table has been created", FqSchemaTableName);

            OnTableCreated(dbCommandFactory);
        }

        journalExists = true;
    }

    /// <summary>
    /// Builds the command that checks for the journal table.
    /// </summary>
    /// <remarks>
    /// Overridden by the providers so the schema and table name travel as bound parameters rather than being
    /// pasted into the statement.
    /// </remarks>
    protected virtual IDbCommand GetDoesTableExistCommand(Func<IDbCommand> dbCommandFactory)
    {
        var command = dbCommandFactory();
        command.CommandText = DoesTableExistSql();
        command.CommandType = CommandType.Text;
        return command;
    }

    protected bool DoesTableExist(Func<IDbCommand> dbCommandFactory)
    {
        Log().LogInformation("Checking whether journal table exists..");

        using (var command = GetDoesTableExistCommand(dbCommandFactory))
        {
            var executeScalar = command.ExecuteScalar();

            return executeScalar switch
            {
                null => false,
                DBNull => false,
                _ => Convert.ToInt32(executeScalar) == 1
            };
        }
    }

    /// <summary>Verify, using database-specific queries, if the table exists in the database.</summary>
    /// <returns>1 if table exists, 0 otherwise</returns>
    protected abstract string DoesTableExistSql();
}
