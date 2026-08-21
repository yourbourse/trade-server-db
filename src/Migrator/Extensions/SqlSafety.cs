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
using YB.TradeServer.Db.Migrator.Execution;

namespace YB.TradeServer.Db.Migrator.Extensions;

/// <summary>
/// Guards the values that end up inside a SQL statement as text rather than as a bound parameter.
/// </summary>
/// <remarks>
/// Neither PostgreSQL nor ClickHouse accepts a bind parameter in place of an identifier, so a statement such as
/// <c>CREATE ROLE x</c> or <c>GRANT ... ON TABLE y</c> has to be assembled as text. Everything that reaches
/// such a statement is therefore checked here first: identifiers must look like identifiers, and the one value
/// that is genuinely free-form - a password - is escaped for the dialect it is written into.
/// </remarks>
public static class SqlSafety
{
    /// <summary>
    /// The intersection of what PostgreSQL and ClickHouse accept as an unquoted identifier, capped at the
    /// PostgreSQL limit of 63 bytes.
    /// </summary>
    private static readonly Regex identifierPattern = new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.Compiled);

    public static bool IsValidIdentifier(string? value)
    {
        return value is not null && identifierPattern.IsMatch(value);
    }

    /// <summary>
    /// Returns the value when it is a plain identifier, and fails the migration when it is not.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="description">What the value is, used in the error message.</param>
    public static string ValidateIdentifier(string? value, string description)
    {
        if (!IsValidIdentifier(value))
        {
            throw new SchemaMigrationException(
                $"{description} '{value}' is not a valid SQL identifier. " +
                "It has to start with a letter or an underscore, continue with letters, digits or underscores, " +
                "and be at most 63 characters long. " +
                "Identifiers are written into DDL as text - no database accepts a bound parameter in their place - so anything else is rejected here.");
        }

        return value!;
    }

    /// <summary>
    /// Escapes a value so it can be embedded in a single quoted SQL string literal.
    /// </summary>
    /// <remarks>
    /// Used for the password in the users scripts, which sits inside <c>'##user_password##'</c> in a file and so
    /// cannot be bound. PostgreSQL escapes a quote by doubling it; ClickHouse uses backslash escapes, so the
    /// backslash itself has to be escaped first.
    /// </remarks>
    public static string EscapeLiteral(DatabaseProvider provider, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return provider switch
        {
            DatabaseProvider.Postgres => value.Replace("'", "''"),
            DatabaseProvider.ClickHouse => value.Replace("\\", "\\\\").Replace("'", "\\'"),
            _ => throw new SchemaMigrationException($"Unsupported provider: {provider}")
        };
    }
}
