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

namespace YB.DbUp.ClickHouse;

/// <summary>
/// An implementation of the <see cref="IJournal"/> interface which tracks version numbers for a
/// ClickHouse database using a table called schema_versions.
/// </summary>
public class ClickHouseTableJournal : TableJournal
{
    /// <summary>
    /// Creates a new ClickHouse table journal.
    /// </summary>
    /// <param name="connectionManager">The ClickHouse connection manager.</param>
    /// <param name="logger">The upgrade logger.</param>
    /// <param name="schemaName">The database that contains the journal table.</param>
    /// <param name="tableName">The name of the journal table.</param>
    /// <param name="cluster">DB cluster name</param>
    public ClickHouseTableJournal(Func<IConnectionManager> connectionManager, Func<IUpgradeLog> logger, string schemaName, string tableName, string? cluster)
        : base(connectionManager, logger, new ClickHouseObjectParser(), schemaName, tableName)
    {
        Cluster = cluster;
        ClusterSuffixCommand = string.IsNullOrEmpty(cluster) ? "" : $@" ON CLUSTER '{cluster}'";
    }

    public string ClusterSuffixCommand { get; set; }

    public string? Cluster { get; set; }

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
            // the delimiter is part of the prefix so "… for reader::" cannot also match "… for reader2::"
            var previousScripts = script.Name[..(script.Name.IndexOf("::", StringComparison.Ordinal) + 2)];

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

    protected IDbCommand GetDeleteScriptCommand(Func<IDbCommand> dbCommandFactory, string scriptNamePrefix)
    {
        var command = dbCommandFactory();

        AddParameter(command, "scriptNamePrefix", scriptNamePrefix);

        command.CommandText = GetDeleteByPrefixScriptSql();
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
SELECT CAST(1 as INT) AS DatabaseExists FROM system.tables WHERE database = {schemaName:String} and name = {tableName:String};
";
    }

    /// <summary>
    /// Removes the journal entries of a re-runnable script, whatever hash they carry.
    /// </summary>
    /// <remarks>
    /// The prefix is a script name taken from a file on disk, so it is bound rather than pasted into the
    /// statement. mutations_sync makes the mutation finish before the replacement row is written, which keeps a
    /// later read of the journal from seeing the entry that is on its way out.
    /// </remarks>
    protected string GetDeleteByPrefixScriptSql()
    {
        return @$"
ALTER TABLE {FqSchemaTableName} {ClusterSuffixCommand} DELETE WHERE startsWith(script_name, {{scriptNamePrefix:String}}) SETTINGS mutations_sync = 2;
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
    /// ClickHouse binds parameters with the <c>{{name:Type}}</c> syntax rather than the <c>@name</c> syntax used
    /// by most other providers, so the parameter names supplied by the base class are re-formatted here.
    /// </remarks>
    protected override string GetInsertJournalEntrySql(string scriptName, string applied)
    {
        return @$"
INSERT INTO {FqSchemaTableName} (script_name, applied_at) VALUES ({{{scriptName}:String}}, CAST({{{applied}:String}} AS DateTime64(6, 'UTC')));
";
    }

    protected override string GetJournalEntriesSql()
    {
        return $@"
SELECT script_name AS ScriptName FROM {FqSchemaTableName} ORDER BY applied_at ASC;
";
    }

    protected override string CreateSchemaTableSql(string quotedPrimaryKeyName)
    {
        var engine = string.IsNullOrEmpty(Cluster) ? "MergeTree" : "ReplicatedMergeTree";

        return @$"
CREATE TABLE IF NOT EXISTS {FqSchemaTableName} {ClusterSuffixCommand}
(
    script_name LowCardinality(String),
    applied_at DateTime64(6, 'UTC')
)
ENGINE = {engine}
PRIMARY KEY (script_name)
ORDER BY (script_name, applied_at);
";
    }
}
