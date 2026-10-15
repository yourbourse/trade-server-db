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

using ClickHouse.Driver;
using Xunit.Abstractions;

namespace YB.TradeServer.Db.Migrator.Tests.Execution.Ticks;

/// <summary>
/// What the migration makes of the ticks table: its definition, re-runs, retention and grants.
/// </summary>
[Trait("Category", "Integration")]
public class TicksMigrationIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : TicksTestBase(configBuilder, output)
{
    /// <summary>
    /// SHOW CREATE TABLE in full: fails on any change to a column, type, codec, default, key, TTL or setting.
    /// </summary>
    [Fact(DisplayName = "Schema Should create the ticks table with the agreed columns, engine, keys and retention")]
    public void Schema_Should_create_the_ticks_table_with_the_agreed_columns_engine_keys_and_retention()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.Equal(AgreedTable(), ShowCreate(connectionString, "ticks"));
    }

    /// <summary>
    /// No cursor table and no view: a ClickHouse pipeline keeps its position on disk and only inserts.
    /// </summary>
    [Fact(DisplayName = "Schema Should create the ticks table and the journal and nothing else")]
    public void Schema_Should_create_the_ticks_table_and_the_journal_and_nothing_else()
    {
        // arrange, act
        var connectionString = Migrate();

        // assert
        Assert.Equal(["schema_versions", "ticks"], Tables(connectionString));
    }

    /// <summary>
    /// The previous retention entry is deleted before the new one is written, so exactly one survives.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep one journal entry per ticks script across a retention change")]
    public void Migration_Should_keep_one_journal_entry_per_ticks_script_across_a_retention_change()
    {
        // arrange
        const string retentionEntry = @"^\[clickhouse/post\] ticks_ttl\.sql::[0-9a-f]{32}$";

        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var before = TicksJournalEntries(request.ConnectionString);

        // act
        AssertSuccessful(CreateRunner().Run(WithRetention(request, 7)));

        // assert, the create script once and untouched, the retention script once under a new hash
        var after = TicksJournalEntries(request.ConnectionString);

        Assert.Collection(before,
            retention => Assert.Matches(retentionEntry, retention),
            create => Assert.Equal("[clickhouse] 20260922_01_create_ticks.sql", create));

        Assert.Collection(after,
            retention => Assert.Matches(retentionEntry, retention),
            create => Assert.Equal(before[1], create));

        Assert.NotEqual(before[0], after[0]);
    }

    /// <summary>
    /// A forced run re-applies the retention and the grants over live data; neither may touch a tick.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep the stored ticks through a re-run and a forced re-run")]
    public void Migration_Should_keep_the_stored_ticks_through_a_re_run_and_a_forced_re_run()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, new Tick(1, At(0)), new Tick(2, At(1), BidPrice: "1.0857"), new Tick(3, At(2), AskVolume: "0"));
        var stored = Books(request.ConnectionString);

        // act
        AssertSuccessful(CreateRunner().Run(request));
        AssertSuccessful(CreateRunner().Run(request with { FullReload = true }));

        // assert
        Assert.Equal(stored, Books(request.ConnectionString));

        // and the exporter still writes after its role was revoked and granted again
        Insert(ExporterConnectionString(request), new Tick(4, At(3)));

        Assert.Equal([1L, 2L, 3L, 4L], Sequences(request.ConnectionString));
    }

    [Fact(DisplayName = "Migration Should create, retain and grant a renamed ticks table")]
    public void Migration_Should_create_retain_and_grant_a_renamed_ticks_table()
    {
        // arrange
        var request = CreateRequest();
        request.Variables["ticks_table"] = "tick_history";

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert
        Assert.Equal(["schema_versions", "tick_history"], Tables(request.ConnectionString));
        Assert.Equal(AgreedTable("tick_history"), ShowCreate(request.ConnectionString, "tick_history"));

        Execute(ExporterConnectionString(request), InsertInto("tick_history", new Tick(1, At(0))));

        Assert.Equal(1, Scalar<long>(ReaderConnectionString(request), "SELECT count() FROM tick_history"));
    }

    /// <summary>
    /// The create script is journalled by name and never runs again, so a forgotten ticks_table fails the run.
    /// </summary>
    [Fact(DisplayName = "Migration Should fail rather than fork a second table when a renamed ticks table is not passed again")]
    public void Migration_Should_fail_rather_than_fork_a_second_table_when_a_renamed_ticks_table_is_not_passed_again()
    {
        // arrange, a deployment that exports into tick_history
        var request = CreateRequest();
        request.Variables["ticks_table"] = "tick_history";
        AssertSuccessful(CreateRunner().Run(request));

        Execute(request.ConnectionString, InsertInto("tick_history", new Tick(1, At(0))));

        // act, a run without the variable
        var forgotten = CreateRunner().Run(request with { Variables = Variables(request.DatabaseName, null) });

        // assert, the retention script trips over the default name; no second table, no tick lost
        var (phase, failed) = forgotten[^1];

        Assert.Equal("post", phase);
        Assert.False(failed.Successful);
        Assert.StartsWith("[clickhouse/post] ticks_ttl.sql::", failed.ErrorScript?.Name);
        Assert.Equal(UnknownTable, Assert.IsType<ClickHouseServerException>(failed.Error).ErrorCode);

        Assert.Equal(["schema_versions", "tick_history"], Tables(request.ConnectionString));
        Assert.Equal(1, Count(request.ConnectionString, "tick_history"));

        // and the corrected run succeeds, but the users phase had already granted on the default name
        AssertSuccessful(CreateRunner().Run(request));

        Assert.Equal(["INSERT ON tick_history", "INSERT ON ticks"], RoleGrants(request.ConnectionString, $"role_{request.DatabaseName}_exporter"));
        Assert.Equal(["SELECT ON tick_history", "SELECT ON ticks"], RoleGrants(request.ConnectionString, $"role_{request.DatabaseName}_ro"));
    }

    /// <summary>
    /// A dry run may not create the database, so the journal is unreadable and every script is reported pending.
    /// </summary>
    [Fact(DisplayName = "Migration Should list the ticks scripts on a dry run and create nothing")]
    public void Migration_Should_list_the_ticks_scripts_on_a_dry_run_and_create_nothing()
    {
        // arrange
        var request = CreateRequest();
        var system = WithDatabase(request.ConnectionString, "system");

        // act
        var dryRun = CreateRunner().Run(request with { DryRun = true });

        // assert, every script in phase order, hashes aside
        AssertSuccessful(dryRun);

        Assert.Equal(
        [
            "[clickhouse] 20260922_01_create_ticks.sql",
            $"[clickhouse/users] exporter.sql for {request.DatabaseName}_exporter",
            $"[clickhouse/users] ro.sql for {request.DatabaseName}_reader",
            "[clickhouse/post] run_on_every_build.sql",
            "[clickhouse/post] ticks_ttl.sql"
        ], dryRun.SelectMany(step => step.Result.Scripts).Select(script => script.Name.Split("::")[0]));

        // and neither the database nor an account exists
        Assert.Equal(0, Scalar<long>(system, $"SELECT count() FROM system.databases WHERE name = '{request.DatabaseName}'"));
        Assert.Equal(0, Scalar<long>(system, $"SELECT count() FROM system.users WHERE startsWith(name, '{request.DatabaseName}')"));
    }

    /// <summary>
    /// A database provisioned by hand from the guide before this repository existed, ticks and all.
    /// </summary>
    [Fact(DisplayName = "Migration Should adopt a ticks table created by hand from the guide")]
    public void Migration_Should_adopt_a_ticks_table_created_by_hand_from_the_guide()
    {
        // arrange
        var request = CreateRequest();
        CreateDatabase(request);
        Execute(request.ConnectionString, HandMadeTicksTable("ReplacingMergeTree"));
        Insert(request.ConnectionString, new Tick(1, At(0)));

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert, the tick survives and the table converges on the one the migration would have made
        Assert.Equal([1L], Sequences(request.ConnectionString));
        Assert.Equal(AgreedTable(), ShowCreate(request.ConnectionString, "ticks"));

        Insert(ExporterConnectionString(request), new Tick(2, At(1)));

        Assert.Equal([1L, 2L], Sequences(request.ConnectionString));
    }

    /// <summary>
    /// IF NOT EXISTS adopts what it finds: a plain MergeTree stays one, and FINAL then fails on it.
    /// </summary>
    [Fact(DisplayName = "Migration Should keep the engine of a ticks table it did not create")]
    public void Migration_Should_keep_the_engine_of_a_ticks_table_it_did_not_create()
    {
        // arrange, made by hand on an engine that never deduplicates
        var request = CreateRequest();
        CreateDatabase(request);
        Execute(request.ConnectionString, HandMadeTicksTable("MergeTree"));

        // act
        AssertSuccessful(CreateRunner().Run(request));

        // assert, the retention is laid on top, the engine is not converted
        Assert.Equal(AgreedTable().Replace("ENGINE = ReplacingMergeTree", "ENGINE = MergeTree"), ShowCreate(request.ConnectionString, "ticks"));

        AssertError(request.ConnectionString, "SELECT count() FROM ticks FINAL", IllegalFinal);
    }

    [Fact(DisplayName = "Retention Should drop a month of ticks once every one of them is older than the retention")]
    public void Retention_Should_drop_a_month_of_ticks_once_every_one_of_them_is_older_than_the_retention()
    {
        // arrange, the default 31 days; 60 days back is always another month than an hour ago
        var connectionString = Migrate();

        Insert(connectionString, new Tick(1, DaysAgo(60)), new Tick(2, DaysAgo(60).AddMinutes(1)));
        Insert(connectionString, new Tick(3, HoursAgo(1)));

        // act
        ApplyRetention(connectionString);

        // assert
        Assert.Equal([3L], Sequences(connectionString));
    }

    [Fact(DisplayName = "Retention Should age a tick by created_time, not batch_time")]
    public void Retention_Should_age_a_tick_by_created_time_not_batch_time()
    {
        // arrange
        var connectionString = Migrate();

        // act, one created long ago and batched now, one created now and batched long ago
        Insert(connectionString, new Tick(1, DaysAgo(60), BatchTime: HoursAgo(1)));
        Insert(connectionString, new Tick(2, HoursAgo(1), BatchTime: DaysAgo(60)));
        ApplyRetention(connectionString);

        // assert
        Assert.Equal([2L], Sequences(connectionString));
    }

    /// <summary>
    /// The cost of ttl_only_drop_parts: retention is a minimum, and only a merge rewriting the part trims it.
    /// </summary>
    [Fact(DisplayName = "Retention Should keep an expired tick while its part still holds a live one")]
    public void Retention_Should_keep_an_expired_tick_while_its_part_still_holds_a_live_one()
    {
        // arrange, a retention cutting mid last month, so one batch straddles the cut inside one partition
        var (cutOff, days) = CutOffMidLastMonth();
        var connectionString = Migrate(days);

        Insert(connectionString, new Tick(1, cutOff.AddDays(-2)), new Tick(2, cutOff.AddDays(2)));

        // act
        ApplyRetention(connectionString);

        // assert, the TTL pass drops parts only whole
        Assert.Equal([1L, 2L], Sequences(connectionString));

        // and a merge that rewrites the part anyway drops the expired row
        Execute(connectionString, "OPTIMIZE TABLE ticks FINAL");

        Assert.Equal([2L], Sequences(connectionString));
    }

    /// <summary>
    /// MODIFY TTL waits for the TTL pass it starts, so the re-run alone removes what the new retention expires.
    /// </summary>
    [Fact(DisplayName = "Retention Should apply a shortened retention to the ticks already stored when the migration re-runs")]
    public void Retention_Should_apply_a_shortened_retention_to_the_ticks_already_stored_when_the_migration_re_runs()
    {
        // arrange, 60 days kept; 35 days back is always another month than an hour ago
        var request = CreateRequest(ticksTtlDays: 60);
        AssertSuccessful(CreateRunner().Run(request));

        Insert(request.ConnectionString, new Tick(1, DaysAgo(35)));
        Insert(request.ConnectionString, new Tick(2, HoursAgo(1)));
        ApplyRetention(request.ConnectionString);

        Assert.Equal([1L, 2L], Sequences(request.ConnectionString));

        // act
        AssertSuccessful(CreateRunner().Run(WithRetention(request, 7)));

        // assert
        Assert.Equal([2L], Sequences(request.ConnectionString));
    }

    /// <summary>
    /// The production path: the exporter's own account, which may insert and do nothing else.
    /// </summary>
    [Fact(DisplayName = "Grants Should let the exporter insert ticks and do nothing else to them")]
    public void Grants_Should_let_the_exporter_insert_ticks_and_do_nothing_else_to_them()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var exporter = ExporterConnectionString(request);

        // act
        Insert(exporter, new Tick(1, At(0)), new Tick(2, At(1)));

        // assert
        Assert.Equal(2, Count(request.ConnectionString, "ticks"));

        AssertDenied(exporter, "SELECT count() FROM ticks");
        AssertDenied(exporter, "SELECT count() FROM ticks FINAL");
        AssertDenied(exporter, "ALTER TABLE ticks DELETE WHERE 1 = 1");
        AssertDenied(exporter, "DELETE FROM ticks WHERE 1 = 1");
        AssertDenied(exporter, "TRUNCATE TABLE ticks");
        AssertDenied(exporter, "OPTIMIZE TABLE ticks FINAL");
        AssertDenied(exporter, "ALTER TABLE ticks MODIFY TTL toDateTime(created_time) + INTERVAL 1 DAY");
        AssertDenied(exporter, "DROP TABLE ticks");

        Assert.Equal(2, Count(request.ConnectionString, "ticks"));
    }

    [Fact(DisplayName = "Grants Should let the reader read ticks and do nothing else to them")]
    public void Grants_Should_let_the_reader_read_ticks_and_do_nothing_else_to_them()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));
        Insert(request.ConnectionString, new Tick(1, At(0)));

        var reader = ReaderConnectionString(request);

        // act, assert
        Assert.Equal(1, Count(reader, "ticks"));
        Assert.Equal(1, Count(reader, "ticks FINAL"));

        AssertDenied(reader, InsertSql(new Tick(2, At(1))));
        AssertDenied(reader, "ALTER TABLE ticks DELETE WHERE 1 = 1");
        AssertDenied(reader, "DELETE FROM ticks WHERE 1 = 1");
        AssertDenied(reader, "TRUNCATE TABLE ticks");
        AssertDenied(reader, "OPTIMIZE TABLE ticks FINAL");
        AssertDenied(reader, "DROP TABLE ticks");

        Assert.Equal(1, Count(request.ConnectionString, "ticks"));
    }

    [Fact(DisplayName = "Grants Should keep both accounts away from the journal and from DDL")]
    public void Grants_Should_keep_both_accounts_away_from_the_journal_and_from_DDL()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var exporter = ExporterConnectionString(request);
        var reader = ReaderConnectionString(request);

        // act, assert, a forged journal entry would hide a script from every later run
        AssertDenied(exporter, "INSERT INTO schema_versions (script_name, applied_at) VALUES ('forged', now64(6))");
        AssertDenied(exporter, "SELECT count() FROM schema_versions");
        AssertDenied(reader, "SELECT count() FROM schema_versions");

        AssertDenied(exporter, "CREATE TABLE side (id UInt64) ENGINE = Memory");
        AssertDenied(reader, "CREATE TABLE side (id UInt64) ENGINE = Memory");
    }

    /// <summary>
    /// Roles are named after the database, so deployments sharing a server cannot reach each other's ticks.
    /// </summary>
    [Fact(DisplayName = "Grants Should confine the accounts of each deployment to its own database")]
    public void Grants_Should_confine_the_accounts_of_each_deployment_to_its_own_database()
    {
        // arrange, two deployments on one server
        var first = CreateRequest();
        var second = CreateRequest();
        AssertSuccessful(CreateRunner().Run(first));
        AssertSuccessful(CreateRunner().Run(second));

        // act, assert
        Insert(ExporterConnectionString(first), new Tick(1, At(0)));

        AssertDenied(ExporterConnectionString(first), InsertInto($"{second.DatabaseName}.ticks", new Tick(1, At(0))));
        AssertDenied(ReaderConnectionString(first), $"SELECT count() FROM {second.DatabaseName}.ticks");

        Assert.Equal(1, Count(first.ConnectionString, "ticks"));
        Assert.Equal(0, Count(second.ConnectionString, "ticks"));
    }

    /// <summary>
    /// ro.sql revokes the exporter role before granting its own, so the account converges on read only.
    /// </summary>
    [Fact(DisplayName = "Grants Should take INSERT away when an exporter is re-provisioned as read only")]
    public void Grants_Should_take_INSERT_away_when_an_exporter_is_re_provisioned_as_read_only()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var account = request.Users.Single(user => user.Access == "exporter");

        // act, the same account, now read only
        AssertSuccessful(CreateRunner().Run(request with { Users = [account with { Access = "ro" }] }));

        // assert
        var connectionString = WithUser(request.ConnectionString, account);

        Assert.Equal(0, Count(connectionString, "ticks"));
        AssertDenied(connectionString, InsertSql(new Tick(1, At(0))));

        // and revoked, not merely left inactive behind DEFAULT ROLE where SET ROLE could bring it back
        Assert.Equal([$"role_{request.DatabaseName}_ro"], GrantedRoles(request.ConnectionString, account.User));
    }

    /// <summary>
    /// DEFAULT ROLE decides what is active at login, whatever else the account was granted before.
    /// </summary>
    [Fact(DisplayName = "Grants Should log a reader re-provisioned as exporter in with the exporter role alone")]
    public void Grants_Should_log_a_reader_re_provisioned_as_exporter_in_with_the_exporter_role_alone()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var account = request.Users.Single(user => user.Access == "ro");

        // act, the same account, now the exporter
        AssertSuccessful(CreateRunner().Run(request with { Users = [account with { Access = "exporter" }] }));

        // assert
        var connectionString = WithUser(request.ConnectionString, account);

        Assert.Equal($"role_{request.DatabaseName}_exporter", Scalar<string>(connectionString, "SELECT arrayStringConcat(currentRoles(), ',')"));

        Insert(connectionString, new Tick(1, At(0)));
        AssertDenied(connectionString, "SELECT count() FROM ticks");
    }

    /// <summary>
    /// The password is re-applied on every run, so a rotation takes effect at once and the old one stops working.
    /// </summary>
    [Fact(DisplayName = "Grants Should switch the exporter to a rotated password")]
    public void Grants_Should_switch_the_exporter_to_a_rotated_password()
    {
        // arrange
        var request = CreateRequest();
        AssertSuccessful(CreateRunner().Run(request));

        var account = request.Users.Single(user => user.Access == "exporter");
        var rotated = account with { Password = "rotated" };

        // act
        AssertSuccessful(CreateRunner().Run(request with { Users = [rotated] }));

        // assert
        Insert(WithUser(request.ConnectionString, rotated), new Tick(1, At(0)));
        AssertError(WithUser(request.ConnectionString, account), InsertSql(new Tick(2, At(1))), AuthenticationFailed);

        Assert.Equal([1L], Sequences(request.ConnectionString));
    }
}
