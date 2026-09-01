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

namespace YB.TradeServer.Db.Migrator.Execution;

/// <summary>
/// The sub folder names a script folder is expected to use, one per migration phase.
/// </summary>
/// <remarks>
/// The names are part of the contract with whoever writes the scripts - a script lands in a phase purely
/// because of the folder it sits in - so they are kept here rather than spelled out at every use.
/// </remarks>
public static class ScriptSubFolders
{
    /// <summary>
    /// Re-runnable scripts that prepare the database before the incremental scripts run.
    /// </summary>
    public const string Pre = "pre";

    /// <summary>
    /// Incremental scripts, which sit directly in the script folder rather than in a sub folder.
    /// </summary>
    public const string? Root = null;

    /// <summary>
    /// Re-runnable view definitions.
    /// </summary>
    public const string Views = "views";

    /// <summary>
    /// Re-runnable scripts creating one account per access level, named after the access level they grant.
    /// </summary>
    public const string Users = "users";

    /// <summary>
    /// Re-runnable scripts that run once everything else is in place.
    /// </summary>
    public const string Post = "post";

    /// <summary>
    /// Stands in for <see cref="Root"/> in logs and results, which have no folder name to show.
    /// </summary>
    public const string RootDisplayName = "(root)";
}
