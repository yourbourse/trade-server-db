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

using YB.DbUp.ClickHouse;
using YB.DbUp.PSQL;

namespace YB.TradeServer.Db.Migrator.Tests.Execution;

public class ScriptSplittingTests
{
    [Fact(DisplayName = "SplitScriptIntoCommands Should split a ClickHouse script on the break marker")]
    public void SplitScriptIntoCommands_Should_split_a_ClickHouse_script_on_the_break_marker()
    {
        // arrange
        const string script = """
                              ----break----

                              CREATE TABLE a (id Int64) ENGINE = MergeTree ORDER BY id;

                              ----break----

                              CREATE TABLE b (id Int64) ENGINE = MergeTree ORDER BY id;
                              """;

        var connectionManager = new ClickHouseConnectionManager("Host=localhost");

        // act
        var statements = connectionManager.SplitScriptIntoCommands(script).ToList();

        // assert
        Assert.Equal(2, statements.Count);
        Assert.StartsWith("CREATE TABLE a", statements[0]);
        Assert.StartsWith("CREATE TABLE b", statements[1]);
    }

    [Fact(DisplayName = "SplitScriptIntoCommands Should drop a ClickHouse chunk holding nothing but comments")]
    public void SplitScriptIntoCommands_Should_drop_a_ClickHouse_chunk_holding_nothing_but_comments()
    {
        // arrange
        const string script = """
                              -- A file header explaining what the script does.
                              -- It sits above the first break and is not a statement.

                              ----break----

                              CREATE TABLE a (id Int64) ENGINE = MergeTree ORDER BY id;
                              """;

        var connectionManager = new ClickHouseConnectionManager("Host=localhost");

        // act
        var statements = connectionManager.SplitScriptIntoCommands(script).ToList();

        // assert
        var statement = Assert.Single(statements);
        Assert.StartsWith("CREATE TABLE a", statement);
    }

    [Fact(DisplayName = "SplitScriptIntoCommands Should keep the comments that introduce a statement")]
    public void SplitScriptIntoCommands_Should_keep_the_comments_that_introduce_a_statement()
    {
        // arrange
        const string script = """
                              ----break----

                              -- creates the trades table
                              CREATE TABLE a (id Int64) ENGINE = MergeTree ORDER BY id;
                              """;

        var connectionManager = new ClickHouseConnectionManager("Host=localhost");

        // act
        var statements = connectionManager.SplitScriptIntoCommands(script).ToList();

        // assert
        var statement = Assert.Single(statements);
        Assert.Contains("-- creates the trades table", statement);
        Assert.Contains("CREATE TABLE a", statement);
    }

    [Fact(DisplayName = "SplitScriptIntoCommands Should not split a ClickHouse statement carrying four dashes")]
    public void SplitScriptIntoCommands_Should_not_split_a_ClickHouse_statement_carrying_four_dashes()
    {
        // arrange, the dashes sit inside a literal, not on a line of their own
        const string script = """
                              ----break----

                              INSERT INTO a (note) VALUES ('a----b');
                              """;

        var connectionManager = new ClickHouseConnectionManager("Host=localhost");

        // act
        var statements = connectionManager.SplitScriptIntoCommands(script).ToList();

        // assert
        var statement = Assert.Single(statements);
        Assert.Contains("'a----b'", statement);
    }

    [Fact(DisplayName = "SplitScriptIntoCommands Should not split a PostgreSQL statement carrying four dashes")]
    public void SplitScriptIntoCommands_Should_not_split_a_PostgreSQL_statement_carrying_four_dashes()
    {
        // arrange
        const string script = "SELECT 'a----b';";

        var connectionManager = new PostgresConnectionManager("Host=localhost");

        // act
        var statements = connectionManager.SplitScriptIntoCommands(script).ToList();

        // assert
        var statement = Assert.Single(statements);
        Assert.Equal("SELECT 'a----b';", statement);
    }

    [Fact(DisplayName = "SplitScriptIntoCommands Should send a PostgreSQL script without a break as one command")]
    public void SplitScriptIntoCommands_Should_send_a_PostgreSQL_script_without_a_break_as_one_command()
    {
        // arrange
        const string script = """
                              CREATE TABLE a (id BIGINT);

                              CREATE INDEX idx_a ON a (id);
                              """;

        var connectionManager = new PostgresConnectionManager("Host=localhost");

        // act
        var statements = connectionManager.SplitScriptIntoCommands(script).ToList();

        // assert
        var statement = Assert.Single(statements);
        Assert.Contains("CREATE TABLE a", statement);
        Assert.Contains("CREATE INDEX idx_a", statement);
    }

    [Fact(DisplayName = "SplitScriptIntoCommands Should split a PostgreSQL script when a break is present")]
    public void SplitScriptIntoCommands_Should_split_a_PostgreSQL_script_when_a_break_is_present()
    {
        // arrange
        const string script = """
                              CREATE TABLE a (id BIGINT);

                              ----break----

                              CREATE INDEX CONCURRENTLY idx_a ON a (id);
                              """;

        var connectionManager = new PostgresConnectionManager("Host=localhost");

        // act
        var statements = connectionManager.SplitScriptIntoCommands(script).ToList();

        // assert
        Assert.Equal(2, statements.Count);
        Assert.StartsWith("CREATE TABLE a", statements[0]);
        Assert.StartsWith("CREATE INDEX CONCURRENTLY", statements[1]);
    }
}
