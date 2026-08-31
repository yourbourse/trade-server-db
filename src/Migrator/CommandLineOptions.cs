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

    // Required is left off so the check can live in DbUpRunner, where the scripts in play are known: the flag is
    // only demanded when the script set carries a users phase, and the message can then name the access levels
    // it implements instead of the parser's generic "a required option is missing"
    [Option('u', "users", Required = false, HelpText = "The users to create or update, format: Access=exporter;User=trade_exporter;Password=passw0rd. Required whenever the scripts carry a users folder, and has to be repeated on every migration - the grants are re-applied per run, so a run without it leaves the accounts without access to any table the migration adds")]
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
