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

using Xunit.Abstractions;
using YB.TradeServer.Db.Migrator.Logging;

namespace YB.TradeServer.Db.Migrator.Tests;

/// <summary>
/// Routes the DbUp output into the test output, so a failing migration shows the SQL that broke it.
/// </summary>
public class TestDbUpLogProvider(ITestOutputHelper output) : IDbUpLogProvider
{
    public void LogTrace(string format, params object[] args) => Write("TRACE", format, args);

    public void LogDebug(string format, params object[] args) => Write("DEBUG", format, args);

    public void LogInformation(string format, params object[] args) => Write("INFO ", format, args);

    public void LogWarning(string format, params object[] args) => Write("WARN ", format, args);

    public void LogError(string format, params object[] args) => Write("ERROR", format, args);

    public void LogError(Exception ex, string format, params object[] args) => Write("ERROR", $"{format}{Environment.NewLine}{ex}", args);

    private void Write(string level, string format, params object[] args)
    {
        try
        {
            output.WriteLine($"[{level}] {(args.Length == 0 ? format : string.Format(format, args))}");
        }
        catch (FormatException)
        {
            output.WriteLine($"[{level}] {format}");
        }
    }
}
