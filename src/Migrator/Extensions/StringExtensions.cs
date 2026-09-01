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

using System.Security.Cryptography;
using System.Text;

namespace YB.TradeServer.Db.Migrator.Extensions;

public static class StringExtensions
{
    /// <summary>
    /// The MD5 hash of the script content, appended to the journal entry of a re-runnable script so a change
    /// to the file makes the script run again.
    /// </summary>
    public static string GetHash(this string s)
    {
        var md5Hash = MD5.HashData(Encoding.UTF8.GetBytes(s));

        var result = new StringBuilder(md5Hash.Length * 2);

        foreach (var t in md5Hash)
        {
            result.Append(t.ToString("x2"));
        }

        return result.ToString();
    }

    public static string ThrowWhenNullOrEmpty(this string? value, string parameterName, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message, parameterName);
        }

        return value;
    }

    /// <summary>
    /// Replaces the value of every sensitive key in a semicolon separated list, so a connection string or a
    /// user definition can be written to the log without leaking the credentials it carries.
    /// </summary>
    public static string MaskSecrets(this string? value, params string[] sensitiveKeys)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries);

        var masked = new List<string>(parts.Length);
        var insideSensitiveValue = false;

        foreach (var part in parts)
        {
            var separatorIndex = part.IndexOf('=');

            if (separatorIndex < 0)
            {
                // a segment with no key of its own continues the previous value - a password carrying a
                // semicolon - so it is masked along with it rather than printed in the clear
                masked.Add(insideSensitiveValue ? "*******" : part);
                continue;
            }

            var key = part[..separatorIndex].Trim();

            insideSensitiveValue = sensitiveKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

            masked.Add(insideSensitiveValue ? $"{key}=*******" : part);
        }

        return string.Join(";", masked);
    }
}
