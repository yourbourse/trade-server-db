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

using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Support;
using Npgsql;

namespace YB.DbUp.PSQL;

/// <summary>
/// An implementation of <see cref="ScriptExecutor"/> that executes against a PostgreSQL database.
/// </summary>
public class PostgresScriptExecutor : ScriptExecutor
{
    /// <summary>
    /// Initializes an instance of the <see cref="PostgresScriptExecutor"/> class.
    /// </summary>
    /// <param name="connectionManagerFactory"></param>
    /// <param name="log">The logging mechanism.</param>
    /// <param name="schema">The schema that contains the table.</param>
    /// <param name="variablesEnabled">Function that returns <c>true</c> if variables should be replaced, <c>false</c> otherwise.</param>
    /// <param name="scriptPreprocessors">Script Preprocessors in addition to variable substitution</param>
    /// <param name="journal"></param>
    public PostgresScriptExecutor(
        Func<IConnectionManager> connectionManagerFactory,
        Func<IUpgradeLog> log,
        string? schema,
        Func<bool> variablesEnabled,
        IEnumerable<IScriptPreprocessor> scriptPreprocessors,
        Func<IJournal> journal)
        : base(connectionManagerFactory, new PostgresObjectParser(), log, schema, variablesEnabled, scriptPreprocessors, journal)
    {
    }

    protected override string GetVerifySchemaSql(string schema)
    {
        return $@"CREATE SCHEMA IF NOT EXISTS ""{schema}"";";
    }

    protected override void ExecuteCommandsWithinExceptionHandler(int index, SqlScript script, Action executeCommand)
    {
        try
        {
            executeCommand();
        }
        catch (PostgresException postgresException)
        {
            Log().LogInformation("Postgres exception has occured in script: '{0}'", script.Name);
            Log().LogError("" +
                "Script block number: {0}; " + Environment.NewLine +
                "   - SQL state:    {1}; " + Environment.NewLine +
                "   - Position:     {2}; " + Environment.NewLine +
                "   - Detail:       {3}; " + Environment.NewLine +
                "   - Hint:         {4}; " + Environment.NewLine +
                "   - Message:      {5}",
                index,
                postgresException.SqlState,
                postgresException.Position,
                postgresException.Detail,
                postgresException.Hint,
                postgresException.MessageText);
            Log().LogError(postgresException.ToString());
            throw;
        }
    }
}
