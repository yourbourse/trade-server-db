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

namespace YB.TradeServer.Db.Migrator.Config;

public record DatabaseUser
{
    /// <summary>
    /// The access level, matching the name of a script file in the users folder, i.e. "exporter" for users/exporter.sql
    /// </summary>
    public string Access { get; set; } = null!;

    public string User { get; set; } = null!;

    public string Password { get; set; } = null!;
}
