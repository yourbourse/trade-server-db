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

using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Extensions;
using YB.TradeServer.Db.Migrator.Handlers;

namespace YB.TradeServer.Db.Migrator.Tests.Handlers;

public class MigrationHandlerTests
{
    [Theory(DisplayName = "ParseProvider Should map every accepted spelling")]
    [InlineData("clickhouse", DatabaseProvider.ClickHouse)]
    [InlineData("ClickHouse", DatabaseProvider.ClickHouse)]
    [InlineData(" ch ", DatabaseProvider.ClickHouse)]
    [InlineData("postgres", DatabaseProvider.Postgres)]
    [InlineData("postgresql", DatabaseProvider.Postgres)]
    [InlineData("psql", DatabaseProvider.Postgres)]
    [InlineData("PG", DatabaseProvider.Postgres)]
    public void ParseProvider_Should_map_every_accepted_spelling(string provider, DatabaseProvider expected)
    {
        // act
        var actual = MigrationHandler.ParseProvider(provider);

        // assert
        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "ParseProvider Should reject an unknown provider")]
    public void ParseProvider_Should_reject_an_unknown_provider()
    {
        // act, assert
        Assert.Throws<ArgumentException>(() => MigrationHandler.ParseProvider("mysql"));
    }

    [Theory(DisplayName = "GetDefaultScriptFolder Should point at the folder of the provider")]
    [InlineData(DatabaseProvider.ClickHouse, "clickhouse")]
    [InlineData(DatabaseProvider.Postgres, "psql")]
    public void GetDefaultScriptFolder_Should_point_at_the_folder_of_the_provider(DatabaseProvider provider, string expected)
    {
        // act
        var actual = MigrationHandler.GetDefaultScriptFolder(provider);

        // assert
        Assert.Equal(expected, actual);
    }

    [Fact(DisplayName = "ParseUsers Should parse an access level, a user and a password")]
    public void ParseUsers_Should_parse_an_access_level_a_user_and_a_password()
    {
        // arrange
        var users = new[]
        {
            "Access=exporter;User=trade_exporter;Password=passw0rd",
            "Access=ro;User=trade_reader;Password=s3cret"
        };

        // act
        var actual = MigrationHandler.ParseUsers(users);

        // assert
        Assert.Equal(2, actual.Count);
        Assert.Equal("exporter", actual[0].Access);
        Assert.Equal("trade_exporter", actual[0].User);
        Assert.Equal("passw0rd", actual[0].Password);
        Assert.Equal("ro", actual[1].Access);
        Assert.Equal("trade_reader", actual[1].User);
        Assert.Equal("s3cret", actual[1].Password);
    }

    [Fact(DisplayName = "ParseUsers Should keep a password containing an equals sign intact")]
    public void ParseUsers_Should_keep_a_password_containing_an_equals_sign_intact()
    {
        // arrange
        var users = new[] { "Access=exporter;User=trade_exporter;Password=a=b=c" };

        // act
        var actual = MigrationHandler.ParseUsers(users);

        // assert
        Assert.Equal("a=b=c", actual[0].Password);
    }

    [Fact(DisplayName = "ParseUsers Should reject an incomplete user definition")]
    public void ParseUsers_Should_reject_an_incomplete_user_definition()
    {
        // arrange
        var users = new[] { "Access=exporter;User=trade_exporter" };

        // act, assert
        Assert.Throws<ArgumentException>(() => MigrationHandler.ParseUsers(users));
    }

    [Fact(DisplayName = "ParseUsers Should return an empty list when no user is supplied")]
    public void ParseUsers_Should_return_an_empty_list_when_no_user_is_supplied()
    {
        // act
        var actual = MigrationHandler.ParseUsers(null);

        // assert
        Assert.Empty(actual);
    }

    [Fact(DisplayName = "ParseKeyValuePairs Should split on the first equals sign only")]
    public void ParseKeyValuePairs_Should_split_on_the_first_equals_sign_only()
    {
        // arrange
        var pairs = new[] { "trades_table=trades_export", "comment=a=b" };

        // act
        var actual = MigrationHandler.ParseKeyValuePairs(pairs);

        // assert
        Assert.Equal(2, actual.Count);
        Assert.Equal("trades_table", actual[0].Key);
        Assert.Equal("trades_export", actual[0].Value);
        Assert.Equal("comment", actual[1].Key);
        Assert.Equal("a=b", actual[1].Value);
    }

    [Fact(DisplayName = "ExportConfiguration Should read the connection details and the table names")]
    public void ExportConfiguration_Should_read_the_connection_details_and_the_table_names()
    {
        // arrange
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "export_config.json");

        // act
        var actual = ExportConfiguration.Load(path);

        // assert
        var pipeline = Assert.Single(actual.Pipelines);
        Assert.Equal("trade", pipeline.Entity);
        Assert.Equal("postgresql", pipeline.Target);
        Assert.Equal(DatabaseProvider.Postgres, MigrationHandler.ParseProvider(pipeline.Target));
        Assert.Equal("db.example.com", pipeline.ExportTarget.Host);
        Assert.Equal(5432, pipeline.ExportTarget.Port);
        Assert.Equal("trading", pipeline.ExportTarget.Database);
        Assert.Equal("exporter", pipeline.ExportTarget.User);
        Assert.Equal("trades_export", pipeline.ExportTarget.Table);
        Assert.Equal("trades_export_cursor", pipeline.ExportTarget.CursorTable);
        Assert.Equal(1, pipeline.ExportTarget.SenderId);
    }

    [Fact(DisplayName = "ExportConfiguration Should fail when the file is missing")]
    public void ExportConfiguration_Should_fail_when_the_file_is_missing()
    {
        // arrange
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "does_not_exist.json");

        // act, assert
        Assert.Throws<FileNotFoundException>(() => ExportConfiguration.Load(path));
    }

    [Fact(DisplayName = "MaskSecrets Should hide the password and keep everything else readable")]
    public void MaskSecrets_Should_hide_the_password_and_keep_everything_else_readable()
    {
        // arrange
        const string connectionString = "Host=localhost;Port=5432;Database=trade;Username=exporter;Password=passw0rd";

        // act
        var actual = connectionString.MaskSecrets("Password");

        // assert
        Assert.Equal("Host=localhost;Port=5432;Database=trade;Username=exporter;Password=*******", actual);
    }

    [Fact(DisplayName = "GetHash Should be stable for the same content and differ for changed content")]
    public void GetHash_Should_be_stable_for_the_same_content_and_differ_for_changed_content()
    {
        // arrange
        const string script = "CREATE TABLE trades (id BIGINT);";

        // act
        var hash = script.GetHash();

        // assert
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, script.GetHash());
        Assert.NotEqual(hash, (script + " ").GetHash());
    }
}
