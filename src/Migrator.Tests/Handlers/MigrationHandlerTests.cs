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

    [Fact(DisplayName = "ParseUsers Should keep a password containing a semicolon intact")]
    public void ParseUsers_Should_keep_a_password_containing_a_semicolon_intact()
    {
        // arrange, the semicolon is the separator of the definition itself, so the password has to survive it
        var users = new[] { @"Access=exporter;User=trade_exporter;Password=p'a\ss'; DROP TABLE trades; --" };

        // act
        var actual = MigrationHandler.ParseUsers(users);

        // assert
        Assert.Equal("exporter", actual[0].Access);
        Assert.Equal("trade_exporter", actual[0].User);
        Assert.Equal(@"p'a\ss'; DROP TABLE trades; --", actual[0].Password);
    }

    [Fact(DisplayName = "ParseUsers Should keep a trailing semicolon in the password")]
    public void ParseUsers_Should_keep_a_trailing_semicolon_in_the_password()
    {
        // arrange
        var users = new[] { "Access=ro;User=trade_reader;Password=passw0rd;" };

        // act
        var actual = MigrationHandler.ParseUsers(users);

        // assert
        Assert.Equal("passw0rd;", actual[0].Password);
    }

    [Fact(DisplayName = "ParseUsers Should read the keys in any order")]
    public void ParseUsers_Should_read_the_keys_in_any_order()
    {
        // arrange
        var users = new[] { "Password=pa;ss;User=trade_reader;Access=ro" };

        // act
        var actual = MigrationHandler.ParseUsers(users);

        // assert
        Assert.Equal("ro", actual[0].Access);
        Assert.Equal("trade_reader", actual[0].User);
        Assert.Equal("pa;ss", actual[0].Password);
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

    [Fact(DisplayName = "MaskSecrets Should hide a password that carries a semicolon")]
    public void MaskSecrets_Should_hide_a_password_that_carries_a_semicolon()
    {
        // arrange, the trailing segment is part of the password, not a key of its own
        const string userDefinition = "Access=exporter;User=trade_exporter;Password=pa;ss";

        // act
        var actual = userDefinition.MaskSecrets("Password");

        // assert
        Assert.Equal("Access=exporter;User=trade_exporter;Password=*******;*******", actual);
    }

    [Fact(DisplayName = "MaskSecrets Should leave a segment that follows a non sensitive key alone")]
    public void MaskSecrets_Should_leave_a_segment_that_follows_a_non_sensitive_key_alone()
    {
        // arrange
        const string connectionString = "Host=localhost;Pooling=true;Timeout=30";

        // act
        var actual = connectionString.MaskSecrets("Password");

        // assert
        Assert.Equal(connectionString, actual);
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

    /// <summary>
    /// The exact set a PostgreSQL run substitutes into the scripts.
    /// </summary>
    /// <remarks>
    /// Deliberately an exact comparison rather than a "contains" check. A script naming a variable the migrator
    /// does not produce is rejected before it reaches the server, so every place that assembles a variable set -
    /// this list, the integration fixture, and the documentation - has to gain the name at the same time. An
    /// exact assertion is what makes adding one here fail loudly instead of somewhere further away.
    /// </remarks>
    private static readonly string[] postgresVariables =
    [
        "assembly_version",
        "database_name",
        "trades_table",
        "cursor_table",
        "daily_statements_table",
        "order_snapshots_table",
        "position_snapshots_table",
        "trading_account_events_table",
        "trading_accounts_table",
        "trading_accounts_resync_view",
        "trades_history_view",
        "cash_transfers_view"
    ];

    [Fact(DisplayName = "BuildVariables Should produce exactly the documented PostgreSQL variables")]
    public void BuildVariables_Should_produce_exactly_the_documented_PostgreSQL_variables()
    {
        // act
        var variables = MigrationHandler.BuildVariables(DatabaseProvider.Postgres, "trading", null, new());

        // assert
        Assert.Equal(postgresVariables.Order(), variables.Keys.Order());
    }

    [Theory(DisplayName = "BuildVariables Should default the trading account objects to their documented names")]
    [InlineData("trading_account_events_table", "trading_account_events")]
    [InlineData("trading_accounts_table", "trading_accounts")]
    [InlineData("trading_accounts_resync_view", "trading_accounts_resync")]
    public void BuildVariables_Should_default_the_trading_account_objects_to_their_documented_names(string variable, string expected)
    {
        // act
        var variables = MigrationHandler.BuildVariables(DatabaseProvider.Postgres, "trading", null, new());

        // assert
        Assert.Equal(expected, variables[variable]);
    }

    [Theory(DisplayName = "BuildVariables Should let --variables rename a trading account object")]
    [InlineData("trading_account_events_table")]
    [InlineData("trading_accounts_table")]
    [InlineData("trading_accounts_resync_view")]
    public void BuildVariables_Should_let_variables_rename_a_trading_account_object(string variable)
    {
        // arrange
        var options = new CommandLineOptions { Variables = [$"{variable}=renamed_object"] };

        // act
        var variables = MigrationHandler.BuildVariables(DatabaseProvider.Postgres, "trading", null, options);

        // assert
        Assert.Equal("renamed_object", variables[variable]);
    }

    /// <summary>
    /// Every one of these names is written into DDL as text, so an unchecked value would travel to the server
    /// verbatim. The guard lives in identifierVariables, and a name added to the defaults but not to that list
    /// would pass this test only if it were validated - which is the point of covering all three.
    /// </summary>
    [Theory(DisplayName = "BuildVariables Should reject a trading account object name that is not an identifier")]
    [InlineData("trading_account_events_table")]
    [InlineData("trading_accounts_table")]
    [InlineData("trading_accounts_resync_view")]
    public void BuildVariables_Should_reject_a_trading_account_object_name_that_is_not_an_identifier(string variable)
    {
        // arrange
        var options = new CommandLineOptions { Variables = [$"{variable}=not a valid name; DROP TABLE trades"] };

        // act, assert
        Assert.Throws<SchemaMigrationException>(
            () => MigrationHandler.BuildVariables(DatabaseProvider.Postgres, "trading", null, options));
    }
}
