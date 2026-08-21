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

using Microsoft.Extensions.Configuration;
using YB.TradeServer.Db.Migrator.Extensions;

namespace YB.TradeServer.Db.Migrator.Tests;

public class ConfigBuilder
{
    public ConfigBuilder()
    {
        var env = EnvironmentHelper.GetAspNetCoreEnvironmentNameOrDefault();

        Configuration = new ConfigurationBuilder()
            .AddJsonFile($"appsettings.{env}.json", true, false)
            .AddEnvironmentVariables()
            .Build();

        ClickHouseConnectionString = Configuration["ClickHouse:ConnectionString"];
        PostgresConnectionString = Configuration["Postgres:ConnectionString"];
        RepositoryRoot = FindRepositoryRoot();
    }

    public IConfigurationRoot Configuration { get; }

    public string? ClickHouseConnectionString { get; }

    public string? PostgresConnectionString { get; }

    /// <summary>
    /// The folder holding the clickhouse and psql script folders. The SQL is not copied into the test output,
    /// so the integration tests point the migrator at the working copy instead.
    /// </summary>
    public string RepositoryRoot { get; }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "clickhouse")) &&
                Directory.Exists(Path.Combine(directory.FullName, "psql")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Unable to locate the repository root starting from {AppContext.BaseDirectory}");
    }
}
