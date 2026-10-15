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

using Npgsql;
using Xunit.Abstractions;

namespace YB.TradeServer.Db.Migrator.Tests.Execution.EventLogs;

/// <summary>
/// What is particular to the client tables: the agreed columns, two amounts held as doubles, enum ordinals, dates far
/// from today, and the accounts of a client.
/// </summary>
/// <remarks>
/// What the four logs share runs against this one in the EventLog suites.
/// </remarks>
[Trait("Category", "Integration")]
public class ClientsIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    /// <summary>
    /// The agreed schema written out: the only test that fails if client_events is reordered or retyped.
    /// </summary>
    [Fact(DisplayName = "Schema Should carry the agreed columns and types in the agreed order")]
    public void Schema_Should_carry_the_agreed_columns_and_types_in_the_agreed_order()
    {
        // arrange, act
        Migrate(EventLog.Clients);

        // assert
        Assert.Equal(
        [
            "id bigint", "time_inserted timestamp with time zone", "sender_id bigint", "event_type smallint",
            "client_id bigint", "version bigint", "client_type integer", "client_status integer",
            "assigned_manager bigint", "time_created timestamp with time zone",
            "time_modified timestamp with time zone", "compliance_approved_by bigint",
            "compliance_client_category character varying(64)", "compliance_date_approval timestamp with time zone",
            "compliance_date_termination timestamp with time zone", "lead_campaign character varying(64)",
            "lead_source character varying(64)", "introducer bigint", "person_title character varying(32)",
            "person_first_name character varying(32)", "person_middle_name character varying(64)",
            "person_last_name character varying(32)", "person_birth_date timestamp with time zone",
            "person_citizenship character varying(64)", "person_tax_id character varying(64)",
            "person_document_type character varying(32)", "person_document_number character varying(32)",
            "person_document_date timestamp with time zone",
            "person_document_expiration_date timestamp with time zone", "person_document_extra character varying(64)",
            "person_employment integer", "person_industry integer", "person_education integer",
            "person_wealth_source integer", "person_annual_income double precision",
            "person_annual_income_currency character varying(16)", "person_net_assets double precision",
            "person_gender integer", "preferred_communication integer", "preferred_language character varying(16)",
            "company_name character varying(64)", "company_registration_number character varying(64)",
            "company_registration_date timestamp with time zone",
            "company_registration_authority character varying(64)", "company_vat character varying(32)",
            "company_lei character varying(32)", "company_license_number character varying(32)",
            "company_license_authority character varying(64)", "company_country character varying(32)",
            "company_registration_address character varying(64)", "company_website character varying(64)",
            "email character varying(64)", "phone character varying(32)", "messengers character varying(128)",
            "social_media character varying(128)", "last_contact_date timestamp with time zone",
            "address_country character varying(32)", "address_postcode character varying(16)",
            "address_street character varying(128)", "address_state character varying(64)",
            "address_city character varying(64)", "client_external_id character varying(64)",
            "comment character varying(256)", "experience_forex integer", "experience_cfd integer",
            "experience_futures integer", "experience_stocks integer", "experience_crypto integer"
        ], ColumnDefinitions(Log.Events));
    }

    /// <summary>
    /// The agreed schema makes both amounts DOUBLE PRECISION, which holds about fifteen significant digits.
    /// </summary>
    [Fact(DisplayName = "Edge case Should store the two amounts as doubles, rounding what a double cannot hold")]
    public void Edge_case_Should_store_the_two_amounts_as_doubles_rounding_what_a_double_cannot_hold()
    {
        // arrange
        Migrate(EventLog.Clients);

        // act, an income a double holds exactly and net assets it cannot
        Export(State(1).With("person_annual_income", "1234567.89").With("person_net_assets", "12345678901234567890.12"));

        // assert
        Assert.Equal(["1234567.89", "1.2345678901234567e+19"], Values(1, "person_annual_income", "person_net_assets"));
    }

    [Fact(DisplayName = "Edge case Should take NaN and infinity as amounts, but refuse one beyond the range of a double")]
    public void Edge_case_Should_take_NaN_and_infinity_as_amounts_but_refuse_one_beyond_the_range_of_a_double()
    {
        // arrange
        Migrate(EventLog.Clients);

        // act, assert, nothing stops an amount that is not a number
        Export(State(1).With("person_annual_income", "NaN").With("person_net_assets", "-Infinity"));

        Assert.Equal(["NaN", "-Infinity"], Values(1, "person_annual_income", "person_net_assets"));

        // while one past the largest double refuses its batch
        AssertRefused(PostgresErrorCodes.NumericValueOutOfRange, () => Export(State(2), State(3).With("person_net_assets", "1e309")));

        Assert.Equal(["7/1 v1"], Rows());
    }

    /// <summary>
    /// Nothing checks the range, so a member the trade server adds later cannot fail the export.
    /// </summary>
    [Fact(DisplayName = "Edge case Should store enum ordinals past the last documented member")]
    public void Edge_case_Should_store_enum_ordinals_past_the_last_documented_member()
    {
        // arrange
        Migrate(EventLog.Clients);

        // act, each one past the last member SCHEMA_REFERENCE lists for it
        Export(State(1)
            .With("client_type", 2)
            .With("client_status", 8)
            .With("person_gender", 3)
            .With("person_employment", 6)
            .With("person_industry", 18)
            .With("person_education", 5)
            .With("person_wealth_source", 5)
            .With("preferred_communication", 4));

        // assert
        Assert.Equal(["2", "8", "3", "6", "18", "5", "5", "4"], Values(1,
            "client_type", "client_status", "person_gender", "person_employment",
            "person_industry", "person_education", "person_wealth_source", "preferred_communication"));
    }

    [Fact(DisplayName = "Edge case Should keep a birth date before 1970 and a document expiry far ahead")]
    public void Edge_case_Should_keep_a_birth_date_before_1970_and_a_document_expiry_far_ahead()
    {
        // arrange
        Migrate(EventLog.Clients);

        // act
        Export(State(1)
            .With("person_birth_date", "1931-05-17 00:00:00+00")
            .With("person_document_expiration_date", "2099-12-31 23:59:59.999999+00"));

        // assert
        Assert.Equal(["1931-05-17 00:00:00.000000", "2099-12-31 23:59:59.999999"],
            Values(1, "to_char(person_birth_date AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US')",
                "to_char(person_document_expiration_date AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US')"));
    }

    /// <summary>
    /// The query SCHEMA_REFERENCE gives for the accounts of each client, and what joining on the id alone would count.
    /// </summary>
    [Fact(DisplayName = "Reading Should count the accounts of each client within its own sender")]
    public void Reading_Should_count_the_accounts_of_each_client_within_its_own_sender()
    {
        // arrange, client 1002 is Smith on one sender and Jones on the other, and Brown holds no account
        Migrate(EventLog.Clients);

        Export(Client(1002, "Smith"), Client(1003, "Brown"), Client(1002, "Jones", sender: OtherSender));
        ExportTo(EventLog.TradingAccounts, Account(5001, client: 1002), Account(5002, client: 1002), Account(6001, client: 1002, sender: OtherSender));

        // act
        var accounts = Strings("""
                               SELECT c.sender_id || ' ' || c.client_id || ' ' || c.person_last_name || ': ' || count(a.trading_account_id)
                               FROM clients c
                                        LEFT JOIN trading_accounts a ON a.sender_id = c.sender_id AND a.client_id = c.client_id
                               GROUP BY c.sender_id, c.client_id, c.person_last_name
                               ORDER BY c.sender_id, c.client_id
                               """);

        // assert
        Assert.Equal(["7 1002 Smith: 2", "7 1003 Brown: 0", "8 1002 Jones: 1"], accounts);

        // and the id alone would hand all three accounts to Smith and to Jones alike
        Assert.Equal(6, Scalar<long>("SELECT count(*) FROM clients c JOIN trading_accounts a ON a.client_id = c.client_id"));
    }

    private LogEvent Client(long id, string lastName, long sender = Sender)
    {
        return State(id, sender: sender).With("person_last_name", lastName);
    }

    private static LogEvent Account(long id, long client, long sender = Sender)
    {
        return Event(EventType.State, sender).With("trading_account_id", id).With("version", 1L).With("client_id", client);
    }
}
