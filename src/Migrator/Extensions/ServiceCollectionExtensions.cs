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
using Microsoft.Extensions.DependencyInjection;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Handlers;
using YB.TradeServer.Db.Migrator.Logging;

namespace YB.TradeServer.Db.Migrator.Extensions;

public static class ServiceCollectionExtensions
{
    public static IConfigurationBuilder SetupDefaultConfiguration(this IConfigurationBuilder? builder)
    {
        var env = EnvironmentHelper.GetAspNetCoreEnvironmentNameOrDefault();

        builder ??= new ConfigurationBuilder();

        return builder.SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile($"appsettings.{env}.json", true, true)
            .AddEnvironmentVariables();
    }

    public static IServiceCollection AddMigrationServices(this IServiceCollection services)
    {
        services.AddScoped<IDbUpLogProvider, DbUpLogProviderWrapper>();
        services.AddScoped<IMigrationHandler, MigrationHandler>();
        services.AddScoped<IDbUpRunner, DbUpRunner>();

        return services;
    }
}
