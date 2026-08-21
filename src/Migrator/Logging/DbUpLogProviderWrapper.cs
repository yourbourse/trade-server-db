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

using Microsoft.Extensions.Logging;

// ReSharper disable TemplateIsNotCompileTimeConstantProblem

namespace YB.TradeServer.Db.Migrator.Logging;

public class DbUpLogProviderWrapper(ILogger<DbUpLogProviderWrapper> logger) : IDbUpLogProvider
{
    public void LogTrace(string format, params object[] args)
    {
        logger.LogTrace(string.Format(format, args));
    }

    public void LogDebug(string format, params object[] args)
    {
        logger.LogDebug(string.Format(format, args));
    }

    public void LogInformation(string format, params object[] args)
    {
        logger.LogInformation(string.Format(format, args));
    }

    public void LogWarning(string format, params object[] args)
    {
        logger.LogWarning(string.Format(format, args));
    }

    public void LogError(string format, params object[] args)
    {
        logger.LogError(string.Format(format, args));
    }

    public void LogError(Exception ex, string format, params object[] args)
    {
        logger.LogError(ex, string.Format(format, args));
    }
}
