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

namespace YB.TradeServer.Db.Migrator.Extensions;

public static class EnvironmentHelper
{
    public static string? GetAspNetCoreEnvironmentName()
    {
        return Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    }

    public static string GetAspNetCoreEnvironmentNameOrDefault()
    {
        return GetAspNetCoreEnvironmentName()?.ToLowerInvariant() ?? "development";
    }

    public static bool IsLocalRun()
    {
        return string.Equals(GetAspNetCoreEnvironmentName(), "Development", StringComparison.InvariantCultureIgnoreCase);
    }
}
