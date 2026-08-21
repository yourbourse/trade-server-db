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

using System.Runtime.InteropServices;
using YB.TradeServer.Db.Migrator.Execution;

namespace YB.TradeServer.Db.Migrator.Tests.Execution;

public class DbUpRunnerTests
{
    [Fact(DisplayName = "DeduplicateFiles Should remove duplicates within the same order")]
    public void DeduplicateFiles_Should_remove_duplicates_within_the_same_order()
    {
        // arrange
        var fileOrders = new List<FileOrder>
        {
            new(GetAbsolutePath(@"psql/users\ro.sql"), 1),
            new(GetAbsolutePath(@"psql/users\ro.sql"), 1)
        };

        // act
        var actualFileOrders = DbUpRunner.DeduplicateFiles(fileOrders);

        // assert
        Assert.Single(actualFileOrders);
        Assert.Equal(GetAbsolutePath(@"psql/users\ro.sql"), actualFileOrders[0].FilePath);
        Assert.Equal(1, actualFileOrders[0].Order);
    }

    [Fact(DisplayName = "DeduplicateFiles Should keep non duplicates in order")]
    public void DeduplicateFiles_Should_keep_non_duplicates_in_order()
    {
        // arrange
        var fileOrders = new List<FileOrder>
        {
            new(GetAbsolutePath(@"psql/users\ro.sql"), 1),
            new(GetAbsolutePath(@"psql/users\exporter.sql"), 1),
            new(GetAbsolutePath(@"custom/users\integration.sql"), 2)
        };

        // act
        var actualFileOrders = DbUpRunner.DeduplicateFiles(fileOrders);

        // assert
        Assert.Equal(3, actualFileOrders.Count);
    }

    [Fact(DisplayName = "DeduplicateFiles Should process back and forward slashes in paths")]
    public void DeduplicateFiles_Should_process_back_and_forward_slashes_in_paths()
    {
        // arrange
        var fileOrders = new List<FileOrder>
        {
            new(GetAbsolutePath(@"psql\users\ro.sql"), 1),
            new(GetAbsolutePath(@"psql/users\exporter.sql"), 1),
            new(GetAbsolutePath(@"custom\users/integration.sql"), 2)
        };

        // act
        var actualFileOrders = DbUpRunner.DeduplicateFiles(fileOrders);

        // assert
        Assert.Equal(3, actualFileOrders.Count);
    }

    [Fact(DisplayName = "DeduplicateFiles Should use last version of script")]
    public void DeduplicateFiles_Should_use_last_version_of_script()
    {
        // arrange
        var fileOrders = new List<FileOrder>
        {
            new(GetAbsolutePath(@"psql/post\run_on_every_build.sql"), 1),
            new(GetAbsolutePath(@"custom/post\run_on_every_build.sql"), 2)
        };

        // act
        var actualFileOrders = DbUpRunner.DeduplicateFiles(fileOrders);

        // assert
        Assert.Single(actualFileOrders);
        Assert.Equal(GetAbsolutePath(@"custom/post\run_on_every_build.sql"), actualFileOrders[0].FilePath);
        Assert.Equal(2, actualFileOrders[0].Order);
    }

    [Fact(DisplayName = "DeduplicateFiles Should keep scripts with the same name in different phases")]
    public void DeduplicateFiles_Should_keep_scripts_with_the_same_name_in_different_phases()
    {
        // arrange
        var fileOrders = new List<FileOrder>
        {
            new(GetAbsolutePath(@"psql/post\trades.sql"), 1),
            new(GetAbsolutePath(@"custom/views\trades.sql"), 2)
        };

        // act
        var actualFileOrders = DbUpRunner.DeduplicateFiles(fileOrders);

        // assert
        Assert.Equal(2, actualFileOrders.Count);
        Assert.Equal(GetAbsolutePath(@"psql/post\trades.sql"), actualFileOrders[0].FilePath);
        Assert.Equal(1, actualFileOrders[0].Order);
        Assert.Equal(GetAbsolutePath(@"custom/views\trades.sql"), actualFileOrders[1].FilePath);
        Assert.Equal(2, actualFileOrders[1].Order);
    }

    [Fact(DisplayName = "DeduplicateFiles Should order files by folder order and then by path")]
    public void DeduplicateFiles_Should_order_files_by_folder_order_and_then_by_path()
    {
        // arrange
        var fileOrders = new List<FileOrder>
        {
            new(GetAbsolutePath(@"custom/post\b.sql"), 2),
            new(GetAbsolutePath(@"psql/post\z.sql"), 1),
            new(GetAbsolutePath(@"psql/post\a.sql"), 1)
        };

        // act
        var actualFileOrders = DbUpRunner.DeduplicateFiles(fileOrders);

        // assert
        Assert.Equal(3, actualFileOrders.Count);
        Assert.EndsWith("a.sql", actualFileOrders[0].FilePath);
        Assert.EndsWith("z.sql", actualFileOrders[1].FilePath);
        Assert.EndsWith("b.sql", actualFileOrders[2].FilePath);
    }

    [Fact(DisplayName = "FindUnresolvedVariables Should report a placeholder the migrator does not know")]
    public void FindUnresolvedVariables_Should_report_a_placeholder_the_migrator_does_not_know()
    {
        // arrange
        const string content = "CREATE TABLE IF NOT EXISTS ##adams_table## (id BIGINT NOT NULL);";

        // act
        var actual = DbUpRunner.FindUnresolvedVariables(content);

        // assert
        var unresolved = Assert.Single(actual);
        Assert.Equal("##adams_table##", unresolved);
    }

    [Fact(DisplayName = "FindUnresolvedVariables Should report every distinct placeholder once")]
    public void FindUnresolvedVariables_Should_report_every_distinct_placeholder_once()
    {
        // arrange
        const string content = "GRANT SELECT ON ##a_table## TO ##a_role##; GRANT INSERT ON ##a_table## TO ##a_role##;";

        // act
        var actual = DbUpRunner.FindUnresolvedVariables(content);

        // assert
        Assert.Equal(2, actual.Count);
        Assert.Contains("##a_table##", actual);
        Assert.Contains("##a_role##", actual);
    }

    [Fact(DisplayName = "FindUnresolvedVariables Should pass a fully substituted script")]
    public void FindUnresolvedVariables_Should_pass_a_fully_substituted_script()
    {
        // arrange
        const string content = "CREATE TABLE IF NOT EXISTS trades (id BIGINT NOT NULL); -- nothing left to substitute";

        // act
        var actual = DbUpRunner.FindUnresolvedVariables(content);

        // assert
        Assert.Empty(actual);
    }

    private static string GetAbsolutePath(string relativePath)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $@"C:\repo\{relativePath}"
            : $"/repo/{relativePath.Replace('\\', '/')}";
    }
}
