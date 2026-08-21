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
using DbUp.Engine.Transactions;

namespace YB.DbUp.ClickHouse;

/// <summary>
/// Manages ClickHouse database connections.
/// </summary>
public class ClickHouseConnectionManager : DatabaseConnectionManager
{
    /// <summary>
    /// Creates a new ClickHouse database connection.
    /// </summary>
    /// <param name="connectionString">The ClickHouse connection string.</param>
    public ClickHouseConnectionManager(string connectionString) : base(
        new DelegateConnectionFactory(_ => new ClickHouseConnection(connectionString)))
    {
    }

    /// <summary>
    /// Splits the statements in the script using the "----" separator.
    /// </summary>
    /// <remarks>
    /// The ClickHouse HTTP interface accepts a single statement per request, so every script has to be
    /// broken down into individual statements before it is sent to the server.
    /// </remarks>
    /// <param name="scriptContents">The contents of the script to split.</param>
    public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents)
    {
        // use script breaks like
        //----break here----
        //----another break----
        var scriptStatements =
            Regex.Split(scriptContents, "----.*", RegexOptions.IgnoreCase | RegexOptions.Multiline)
                .Select(x => x.Trim())
                .Where(ContainsExecutableStatement)
                .ToArray();

        return scriptStatements;
    }

    /// <summary>
    /// A chunk holding nothing but comments - a file header sitting above the first break, for instance - is
    /// not a statement. ClickHouse rejects it with "Empty query", so it is dropped here instead.
    /// </summary>
    private static bool ContainsExecutableStatement(string statement)
    {
        var withoutLineComments = Regex.Replace(statement, @"^\s*--.*$", "", RegexOptions.Multiline);

        return !string.IsNullOrWhiteSpace(withoutLineComments);
    }
}
