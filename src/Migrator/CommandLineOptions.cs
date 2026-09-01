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

using CommandLine;

namespace YB.TradeServer.Db.Migrator;

public record CommandLineOptions
{
    [Option('p', "provider", Required = true, HelpText = "Database engine, supported values: 'clickhouse', 'postgres'")]
    public string? Provider { get; set; }

    [Option('l', "connection-string", Required = false, HelpText = "The connection string of the database to migrate. Falls back to the TSDB_CONNECTION_STRING environment variable")]
    public string? ConnectionString { get; set; }

    [Option('s', "scripts", Required = false, HelpText = "Scripts (folders) to be processed. Defaults to a single folder named after the provider")]
    public IEnumerable<string>? ScriptFolders { get; set; }

    [Option('r', "scripts-root", Required = false, HelpText = "The folder the script folders are resolved against. Defaults to the current directory, then to the folder holding the Migrator binary")]
    public string? ScriptsRoot { get; set; }

    [Option('c', "cluster", Required = false, HelpText = "ClickHouse only. The name of the cluster to use. When empty a single instance is assumed")]
    public string? Cluster { get; set; }

    // The parser's Required is left off deliberately: whether the flag is needed depends on the scripts in play,
    // which are only known once DbUpRunner has walked the folders. The check lives there instead, so it can be
    // skipped for a script set carrying no users phase and can name the access levels that do exist rather than
    // emitting the parser's bare "Required option 'u, users' is missing". The help text leads with Required all
    // the same, so the option reads as mandatory here - which, for the folders this repository ships, it is.
    [Option('u', "users", Required = false, HelpText = "Required on every migration, not only the first. The users to create or update, format: Access=exporter;User=trade_exporter;Password=passw0rd. The grants are re-applied per run, so a run without this flag leaves the accounts without access to any table the migration adds - and is refused. Only a script set carrying no users folder may omit it")]
    public IEnumerable<string>? Users { get; set; }

    [Option('v', "variables", Required = false, HelpText = "Additional template variables, format: key=value. Substituted into the scripts as ##key##")]
    public IEnumerable<string>? Variables { get; set; }

    [Option('j', "journal-table", Required = false, HelpText = "The name of the table recording applied scripts")]
    public string JournalTable { get; set; } = "schema_versions";

    [Option('f', "force", Required = false, HelpText = "Re-applies every re-runnable script (pre, views, users, post) even when its content has not changed")]
    public bool FullReload { get; set; }

    [Option('n', "dry-run", Required = false, HelpText = "Reports the scripts that would be applied without executing any of them")]
    public bool DryRun { get; set; }
}
