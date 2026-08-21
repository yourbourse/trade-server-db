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

namespace YB.TradeServer.Db.Migrator.Tests.Extensions;

public class SqlSafetyTests
{
    [Theory(DisplayName = "ValidateIdentifier Should accept a plain identifier")]
    [InlineData("trades")]
    [InlineData("trades_export")]
    [InlineData("_leading_underscore")]
    [InlineData("Mixed_Case_9")]
    public void ValidateIdentifier_Should_accept_a_plain_identifier(string identifier)
    {
        // act
        var actual = SqlSafety.ValidateIdentifier(identifier, "test");

        // assert
        Assert.Equal(identifier, actual);
    }

    [Theory(DisplayName = "ValidateIdentifier Should reject anything that could break out of a statement")]
    [InlineData("trades; DROP TABLE trades")]
    [InlineData("trades\"; DROP TABLE trades; --")]
    [InlineData("trades' OR '1'='1")]
    [InlineData("trades--")]
    [InlineData("trades table")]
    [InlineData("9starts_with_a_digit")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateIdentifier_Should_reject_anything_that_could_break_out_of_a_statement(string? identifier)
    {
        // act, assert
        var exception = Assert.Throws<SchemaMigrationException>(() => SqlSafety.ValidateIdentifier(identifier, "test"));
        Assert.Contains("not a valid SQL identifier", exception.Message);
    }

    [Fact(DisplayName = "ValidateIdentifier Should reject an identifier longer than 63 characters")]
    public void ValidateIdentifier_Should_reject_an_identifier_longer_than_63_characters()
    {
        // arrange
        var identifier = new string('a', 64);

        // act, assert
        Assert.Throws<SchemaMigrationException>(() => SqlSafety.ValidateIdentifier(identifier, "test"));
    }

    [Fact(DisplayName = "EscapeLiteral Should double a quote for PostgreSQL")]
    public void EscapeLiteral_Should_double_a_quote_for_PostgreSQL()
    {
        // act
        var actual = SqlSafety.EscapeLiteral(DatabaseProvider.Postgres, "pa'ss");

        // assert
        Assert.Equal("pa''ss", actual);
    }

    [Fact(DisplayName = "EscapeLiteral Should neutralise a PostgreSQL statement break attempt")]
    public void EscapeLiteral_Should_neutralise_a_PostgreSQL_statement_break_attempt()
    {
        // arrange
        const string password = "x'; DROP TABLE trades; --";

        // act
        var actual = SqlSafety.EscapeLiteral(DatabaseProvider.Postgres, password);

        // assert
        Assert.Equal("x''; DROP TABLE trades; --", actual);
        Assert.DoesNotContain("x';", actual);
    }

    [Fact(DisplayName = "EscapeLiteral Should backslash escape for ClickHouse")]
    public void EscapeLiteral_Should_backslash_escape_for_ClickHouse()
    {
        // act
        var actual = SqlSafety.EscapeLiteral(DatabaseProvider.ClickHouse, "pa'ss");

        // assert
        Assert.Equal(@"pa\'ss", actual);
    }

    [Fact(DisplayName = "EscapeLiteral Should escape the backslash before the quote for ClickHouse")]
    public void EscapeLiteral_Should_escape_the_backslash_before_the_quote_for_ClickHouse()
    {
        // arrange, a trailing backslash would otherwise escape the closing quote of the literal
        const string password = @"pass\";

        // act
        var actual = SqlSafety.EscapeLiteral(DatabaseProvider.ClickHouse, password);

        // assert
        Assert.Equal(@"pass\\", actual);
    }

    [Fact(DisplayName = "EscapeLiteral Should leave an ordinary password untouched")]
    public void EscapeLiteral_Should_leave_an_ordinary_password_untouched()
    {
        // act, assert
        Assert.Equal("passw0rd", SqlSafety.EscapeLiteral(DatabaseProvider.Postgres, "passw0rd"));
        Assert.Equal("passw0rd", SqlSafety.EscapeLiteral(DatabaseProvider.ClickHouse, "passw0rd"));
    }
}
