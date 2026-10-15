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
/// What the exporter and the reader may do to each log, its derived table and its resync view.
/// </summary>
/// <remarks>
/// The exporter keeps UPDATE and DELETE on the derived table, because the trigger runs with its rights.
/// </remarks>
[Trait("Category", "Integration")]
public class EventLogGrantsIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    [Theory(DisplayName = "Grants Should let the exporter append to the log and read it, but not rewrite it or read the view")]
    [EveryEventLog]
    public void Grants_Should_let_the_exporter_append_to_the_log_and_read_it_but_not_rewrite_it_or_read_the_view(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1));

        // act, assert
        Assert.Equal(1, Scalar<long>(Exporter, $"SELECT count(*) FROM {Log.Events}"));

        AssertDenied(Exporter, $"UPDATE {Log.Events} SET version = 9");
        AssertDenied(Exporter, $"DELETE FROM {Log.Events}");
        AssertDenied(Exporter, $"TRUNCATE {Log.Events}");
        AssertDenied(Exporter, $"TRUNCATE {Log.State}");
        AssertDenied(Exporter, $"SELECT count(*) FROM {Log.ResyncView}");

        // and the log is as it was sent
        Assert.Equal(["state 1"], EventsInLogOrder());
    }

    /// <summary>
    /// Every way a writer could have its batches stored and not projected needs the owner or a superuser.
    /// </summary>
    [Theory(DisplayName = "Grants Should keep the exporter from switching the projection off")]
    [EveryEventLog]
    public void Grants_Should_keep_the_exporter_from_switching_the_projection_off(EventLog log)
    {
        // arrange
        Migrate(log);

        // act, assert
        AssertDenied(Exporter, $"ALTER TABLE {Log.Events} DISABLE TRIGGER {Log.Trigger}");
        AssertDenied(Exporter, $"DROP TRIGGER {Log.Trigger} ON {Log.Events}");
        AssertDenied(Exporter, $"CREATE OR REPLACE FUNCTION {Log.Function}() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NULL; END'");
        AssertDenied(Exporter, "SET session_replication_role = replica");

        // so its next batch is projected as ever
        Export(State(1));

        Assert.Equal(["7/1 v1"], Rows());
    }

    [Theory(DisplayName = "Grants Should let the reader read the log, the derived table and the view, and change none of them")]
    [EveryEventLog]
    public void Grants_Should_let_the_reader_read_the_log_the_derived_table_and_the_view_and_change_none_of_them(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1));
        Export(ResyncStarted());

        // act, assert
        Assert.Equal(2, Scalar<long>(Reader, $"SELECT count(*) FROM {Log.Events}"));
        Assert.Equal(1, Scalar<long>(Reader, $"SELECT count(*) FROM {Log.State}"));
        Assert.True(Scalar<bool>(Reader, $"SELECT resync_in_progress FROM {Log.ResyncView}"));

        AssertRefused(PostgresErrorCodes.InsufficientPrivilege, () => ExportAs(Reader, State(2)), "a COPY into the log");
        AssertDenied(Reader, $"UPDATE {Log.State} SET version = 9");
        AssertDenied(Reader, $"DELETE FROM {Log.State}");
        AssertDenied(Reader, $"TRUNCATE {Log.Events}");
        AssertDenied(Reader, $"TRUNCATE {Log.State}");

        Assert.Equal(["7/1 v1"], Rows());
    }

    /// <summary>
    /// ro.sql revokes everything on the log, the table and the view before granting SELECT back.
    /// </summary>
    [Theory(DisplayName = "Grants Should take write access away when an exporter is re-provisioned as read only")]
    [EveryEventLog]
    public void Grants_Should_take_write_access_away_when_an_exporter_is_re_provisioned_as_read_only(EventLog log)
    {
        // arrange
        Migrate(log);
        Export(State(1));

        var account = Request.Users.Single(user => user.Access == "exporter");

        // act, the same account, now read only
        AssertSuccessful(Run(Request with { Users = [account with { Access = "ro" }] }));

        // assert, it reads what a reader reads and writes nothing
        Assert.Equal(1, Scalar<long>(Exporter, $"SELECT count(*) FROM {Log.State}"));
        Assert.False(Scalar<bool>(Exporter, $"SELECT EXISTS (SELECT FROM {Log.ResyncView})"));

        AssertRefused(PostgresErrorCodes.InsufficientPrivilege, () => ExportAs(Exporter, State(2)), "a COPY into the log");
        AssertDenied(Exporter, $"UPDATE {Log.State} SET version = 9");
        AssertDenied(Exporter, $"DELETE FROM {Log.State}");
    }
}
