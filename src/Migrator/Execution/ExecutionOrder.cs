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

namespace YB.TradeServer.Db.Migrator.Execution;

public record ExecutionOrder
{
    public int OrderId { get; init; }

    public string? SubFolder { get; init; }

    public bool ReRunnable { get; init; }

    public Func<ExecutionStep, FileOrder, Dictionary<string, string>, DbMigratorRequest, IEnumerable<SqlScript>> FileContentResolver { get; set; } = null!;
}
