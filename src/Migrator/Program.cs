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

#region

using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using YB.TradeServer.Db.Migrator;
using YB.TradeServer.Db.Migrator.Extensions;
using YB.TradeServer.Db.Migrator.Handlers;

#endregion

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("System", LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(
        new RenderedCompactJsonFormatter(),
        Path.Combine("logs", "migrator.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 1L * 1024 * 1024 * 1024, // 1GB
        retainedFileCountLimit: 10, // keep the last 10 files
        rollOnFileSizeLimit: true)
    .CreateLogger();

var arguments = Parser.Default.ParseArguments<CommandLineOptions>(args);

if (arguments.Errors.Any())
{
    Log.Error("Some settings are missing or invalid. Check the error messages above");
    await Log.CloseAndFlushAsync();
    return 1;
}

Console.WriteLine("Migration arguments:" + Environment.NewLine +
    $" --provider {arguments.Value.Provider}" + Environment.NewLine +
    $" --connection-string {(string.IsNullOrEmpty(arguments.Value.ConnectionString) ? $"N/A (using {MigrationHandler.ConnectionStringEnvironmentVariable})" : arguments.Value.ConnectionString.MaskSecrets("Password", "Pwd"))}" + Environment.NewLine +
    $" --scripts [{string.Join(", ", arguments.Value.ScriptFolders ?? [])}]" + Environment.NewLine +
    $" --scripts-root {arguments.Value.ScriptsRoot ?? "N/A (using the current directory)"}" + Environment.NewLine +
    $" --cluster {arguments.Value.Cluster ?? "N/A (a single instance will be used)"}" + Environment.NewLine +
    $" --users [{string.Join(", ", (arguments.Value.Users ?? []).Select(user => user.MaskSecrets("Password")))}]" + Environment.NewLine +
    $" --variables [{string.Join(", ", arguments.Value.Variables ?? [])}]" + Environment.NewLine +
    $" --journal-table {arguments.Value.JournalTable}" + Environment.NewLine +
    $" --force {arguments.Value.FullReload}" + Environment.NewLine +
    $" --dry-run {arguments.Value.DryRun}"
);

var host = Host.CreateDefaultBuilder()
    .ConfigureAppConfiguration(builder => builder.SetupDefaultConfiguration())
    .ConfigureServices((_, services) => services.AddMigrationServices())
    .UseSerilog()
    .Build();

try
{
    host.Services.GetRequiredService<IMigrationHandler>().Handle(arguments.Value);

    Log.Information("Migration completed");

    return 0;
}
catch (Exception e)
{
    host.Services.GetRequiredService<ILogger<Program>>()
        .LogError(e, "Migration failed");

    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// required so ILogger<Program> can be resolved from a file with top level statements
public partial class Program;
