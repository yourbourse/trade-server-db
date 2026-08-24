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
using DbUp.Engine.Transactions;
using Npgsql;

namespace YB.DbUp.PSQL;

/// <summary>
/// Manages PostgreSQL database connections.
/// </summary>
public class PostgresConnectionManager : DatabaseConnectionManager
{
    /// <summary>
    /// A separator is a whole line of four or more dashes. Anchoring it to the line keeps four dashes inside a
    /// statement - <c>SELECT 'a----b'</c>, or a comment drawing a rule - from splitting the command in two.
    /// </summary>
    private const string SeparatorPattern = @"^[ \t]*----.*$";

    /// <summary>
    /// Creates a new PostgreSQL database connection.
    /// </summary>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    public PostgresConnectionManager(string connectionString) : base(
        new DelegateConnectionFactory(_ => new NpgsqlConnection(connectionString)))
    {
    }

    /// <summary>
    /// Splits the statements in the script using the "----" separator.
    /// </summary>
    /// <remarks>
    /// Npgsql happily executes a multi-statement command, so a script without any separator is sent to the
    /// server as a single command - which keeps it inside one implicit transaction. Add separators only when
    /// the statements have to run on their own, for example <c>CREATE INDEX CONCURRENTLY</c>, which
    /// PostgreSQL refuses to run inside a transaction block.
    /// </remarks>
    /// <param name="scriptContents">The contents of the script to split.</param>
    public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents)
    {
        // use script breaks like
        //----break here----
        //----another break----
        var scriptStatements =
            Regex.Split(scriptContents, SeparatorPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline)
                .Select(x => x.Trim())
                .Where(ContainsExecutableStatement)
                .ToArray();

        return scriptStatements;
    }

    /// <summary>
    /// A chunk holding nothing but comments - a file header sitting above the first break, for instance - is
    /// not a statement and is dropped rather than sent to the server.
    /// </summary>
    private static bool ContainsExecutableStatement(string statement)
    {
        var withoutLineComments = Regex.Replace(statement, @"^\s*--.*$", "", RegexOptions.Multiline);

        return !string.IsNullOrWhiteSpace(withoutLineComments);
    }
}
