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

using DbUp.Engine;

namespace YB.DbUp.PSQL;

/// <summary>
/// This preprocessor makes adjustments to your sql to make it compatible with PostgreSQL.
/// </summary>
public class PostgresPreprocessor : IScriptPreprocessor
{
    /// <summary>
    /// Performs some preprocessing step on a PostgreSQL script.
    /// </summary>
    public string Process(string contents) => contents;
}
