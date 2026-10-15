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

using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using DbUp.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;
using YB.TradeServer.Db.Migrator.Config;
using YB.TradeServer.Db.Migrator.Execution;
using YB.TradeServer.Db.Migrator.Handlers;

namespace YB.TradeServer.Db.Migrator.Tests.Execution.EventLogs;

/// <summary>
/// The vocabulary the event log suites are written in - export a batch, read the derived table, try a grant - over
/// a throwaway database per test.
/// </summary>
/// <remarks>
/// A test starts with <see cref="Migrate"/>, which also picks the log the other helpers act on. xUnit builds an instance
/// per test, so both are the test's own; every database and its two accounts are dropped afterwards.
/// </remarks>
public abstract class EventLogTestBase(ConfigBuilder configBuilder, ITestOutputHelper output) : IClassFixture<ConfigBuilder>, IAsyncLifetime
{
    protected const long Sender = 7;
    protected const long OtherSender = 8;

    private const string Epoch = "1970-01-01 00:00:00+00";

    private readonly List<(string ConnectionString, string DatabaseName)> provisionedDatabases = [];
    private readonly Dictionary<string, List<LogColumn>> columnsByTable = [];
    private DbMigratorRequest? request;
    private EventLogTables? tables;

    /// <summary>
    /// The log the test migrated, under the names the migration gave it.
    /// </summary>
    protected EventLogTables Log => tables ?? throw new InvalidOperationException("Call Migrate first.");

    protected DbMigratorRequest Request => request ?? throw new InvalidOperationException("Call Migrate first.");

    /// <summary>
    /// The account the migration ran as, which owns every object.
    /// </summary>
    protected string Owner => Request.ConnectionString;

    /// <summary>
    /// The account the trade server exports as.
    /// </summary>
    protected string Exporter => WithUser(Request.Users.Single(user => user.Access == "exporter"));

    /// <summary>
    /// The read-only reporting account.
    /// </summary>
    protected string Reader => WithUser(Request.Users.Single(user => user.Access == "ro"));

    /// <summary>
    /// The columns the exporter writes, in table order: all but id and time_inserted.
    /// </summary>
    protected IReadOnlyList<LogColumn> LogColumns => ColumnsOf(Log.Events);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (connectionString, databaseName) in provisionedDatabases)
        {
            try
            {
                await using var connection = new NpgsqlConnection(WithDatabase(connectionString, "postgres"));
                await connection.OpenAsync();

                // roles are cluster wide, so dropping the database alone would leave the two accounts behind
                foreach (var statement in new[]
                         {
                             $@"DROP DATABASE IF EXISTS ""{databaseName}"" WITH (FORCE)",
                             $"DROP ROLE IF EXISTS {databaseName}_exporter, {databaseName}_reader"
                         })
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync();
                }
            }
            catch (Exception e)
            {
                output.WriteLine($"Unable to drop {databaseName}: {e.Message}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Migrating
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Provisions a database, migrates it, and makes <paramref name="log"/> the log the other helpers act on.
    /// </summary>
    /// <param name="renamed">Migrates the log under names of the deployment's choosing, passed as --variables.</param>
    protected void Migrate(EventLog log, bool renamed = false)
    {
        var defaults = EventLogTables.Of(log);
        var (chosen, variables) = defaults.Renamed();

        tables = renamed ? chosen : defaults;
        request = CreateRequest(renamed ? variables : []);

        AssertSuccessful(Run(request));
    }

    /// <summary>
    /// Runs the migration over the test's database again, as the next upgrade would.
    /// </summary>
    protected void MigrateAgain(bool force = false)
    {
        AssertSuccessful(Run(Request with { FullReload = force }));
    }

    /// <summary>
    /// Runs a migration and returns what each phase did, without judging it.
    /// </summary>
    protected List<(string SubFolder, DatabaseUpgradeResult Result)> Run(DbMigratorRequest migration)
    {
        return new DbUpRunner(NullLogger<DbUpRunner>.Instance, new TestDbUpLogProvider(output)).Run(migration);
    }

    /// <summary>
    /// The variables the CLI derives, plus any --variables, so a variable added to the migrator reaches these tests.
    /// </summary>
    protected static Dictionary<string, string> Variables(string databaseName, params string[] overrides)
    {
        return MigrationHandler.BuildVariables(DatabaseProvider.Postgres, databaseName, null, new() { Variables = overrides });
    }

    protected static void AssertSuccessful(IEnumerable<(string SubFolder, DatabaseUpgradeResult Result)> results)
    {
        foreach (var (subFolder, result) in results)
        {
            Assert.True(result.Successful, $"{subFolder} failed on '{result.ErrorScript?.Name}': {result.Error}");
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Writing the log, as the trade server does
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// An entity's state, as its creation or a change carries it.
    /// </summary>
    protected LogEvent State(long id, long version = 1, string label = "", long sender = Sender)
    {
        return Event(EventType.State, sender).With(Log.IdColumn, id).With("version", version).With(Log.LabelColumn, label);
    }

    protected LogEvent Deleted(long id, long sender = Sender)
    {
        return Event(EventType.Deletion, sender).With(Log.IdColumn, id);
    }

    protected static LogEvent ResyncStarted(long sender = Sender)
    {
        return Event(EventType.ResyncStarted, sender);
    }

    protected static LogEvent ResyncFinished(long sender = Sender)
    {
        return Event(EventType.ResyncFinished, sender);
    }

    /// <summary>
    /// An event of the given type with nothing else set, as the exporter writes a marker.
    /// </summary>
    protected static LogEvent Event(int eventType, long sender = Sender)
    {
        return new(sender, eventType, ImmutableDictionary<string, object?>.Empty);
    }

    /// <summary>
    /// Sends one batch as the trade server does: a COPY as the exporter, committed on its own.
    /// </summary>
    /// <returns>The ids the log gave the events, in the order they were sent.</returns>
    protected List<long> Export(params LogEvent[] batch)
    {
        ExportAs(Exporter, batch);

        return Longs($"SELECT id FROM (SELECT id FROM {Log.Events} ORDER BY id DESC LIMIT {batch.Length}) batch ORDER BY id");
    }

    protected void ExportAs(string account, params LogEvent[] batch)
    {
        using var connection = Open(account);
        Copy(connection, Log, [], batch);
    }

    /// <summary>
    /// Sends one batch into another log of the same database.
    /// </summary>
    protected void ExportTo(EventLog log, params LogEvent[] batch)
    {
        using var connection = Open(Exporter);
        Copy(connection, EventLogTables.Of(log), [], batch);
    }

    /// <summary>
    /// Sends one batch whose COPY leaves a column out, as an exporter built before the column existed would.
    /// </summary>
    protected void ExportLeavingOut(string column, params LogEvent[] batch)
    {
        using var connection = Open(Exporter);
        Copy(connection, Log, [column], batch);
    }

    /// <summary>
    /// Writes a batch over <c>COPY … FROM STDIN</c>, inside whatever transaction the connection has open.
    /// </summary>
    protected void Copy(NpgsqlConnection connection, params LogEvent[] batch)
    {
        Copy(connection, Log, [], batch);
    }

    /// <summary>
    /// The column list the exporter's COPY names.
    /// </summary>
    protected string CopyColumns()
    {
        return string.Join(", ", LogColumns.Select(column => column.Name));
    }

    /// <summary>
    /// One event as a COPY text line, in the order of <see cref="CopyColumns"/>.
    /// </summary>
    protected string CopyLine(LogEvent logEvent)
    {
        return CopyLine(logEvent, Log, LogColumns);
    }

    private void Copy(NpgsqlConnection connection, EventLogTables into, IReadOnlyCollection<string> leftOut, LogEvent[] batch)
    {
        var columns = ColumnsOf(into.Events).Where(column => !leftOut.Contains(column.Name)).ToList();

        using var writer = connection.BeginTextImport($"COPY {into.Events} ({string.Join(", ", columns.Select(column => column.Name))}) FROM STDIN");

        foreach (var logEvent in batch)
        {
            writer.Write(CopyLine(logEvent, into, columns));
            writer.Write('\n');
        }
    }

    private string CopyLine(LogEvent logEvent, EventLogTables into, IEnumerable<LogColumn> columns)
    {
        var unknown = logEvent.Values.Keys.Where(name => ColumnsOf(into.Events).All(column => column.Name != name)).ToList();

        if (unknown.Count > 0)
        {
            throw new ArgumentException($"{into.Events} has no column {string.Join(", ", unknown)}");
        }

        return string.Join('\t', columns.Select(column => logEvent.Values.TryGetValue(column.Name, out var value)
            ? CopyText(value)
            : column.Name switch
            {
                "sender_id" => CopyText(logEvent.Sender),
                "event_type" => CopyText(logEvent.EventType),
                _ => Zero(column)
            }));
    }

    /// <summary>
    /// A value as COPY's text format spells it. A string goes through as written, so it can carry any literal.
    /// </summary>
    private static string CopyText(object? value)
    {
        return value switch
        {
            null => @"\N",
            string text => text.Replace(@"\", @"\\").Replace("\t", @"\t").Replace("\n", @"\n").Replace("\r", @"\r"),
            bool flag => flag ? "t" : "f",
            DateTime time => time.Kind == DateTimeKind.Utc
                ? time.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "+00"
                : throw new ArgumentException("A time has to be UTC to go into the log."),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"No COPY text for a {value.GetType().Name}.")
        };
    }

    /// <summary>
    /// What the exporter writes into a column that does not apply.
    /// </summary>
    private static string Zero(LogColumn column)
    {
        return column switch
        {
            { Nullable: true } => @"\N",
            { Type: "character varying" } => "",
            { Type: "timestamp with time zone" } => Epoch,
            { Type: "boolean" } => "f",
            _ => "0"
        };
    }

    // ---------------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The derived table as <c>sender/id vVersion label</c>, one line per row in key order.
    /// </summary>
    protected List<string> Rows()
    {
        return Strings(RowsSql(withStamps: false));
    }

    /// <summary>
    /// <see cref="Rows"/>, each ending in <c>#id</c> of the event it was written from.
    /// </summary>
    protected List<string> RowsWithStamps()
    {
        return Strings(RowsSql(withStamps: true));
    }

    private string RowsSql(bool withStamps)
    {
        return $"""
                SELECT sender_id || '/' || {Log.IdColumn} || ' v' || version
                       || CASE WHEN {Log.LabelColumn} = '' THEN '' ELSE ' ' || {Log.LabelColumn} END
                       {(withStamps ? "|| ' #' || source_event_row_id" : "")}
                FROM {Log.State}
                ORDER BY sender_id, {Log.IdColumn}
                """;
    }

    /// <summary>
    /// The log in id order, one line per event: <c>state 1</c>, <c>deletion 1</c>, <c>resync started</c>.
    /// </summary>
    protected List<string> EventsInLogOrder()
    {
        return Strings($"""
                        SELECT CASE event_type
                                   WHEN 0 THEN 'state ' || {Log.IdColumn}
                                   WHEN 1 THEN 'deletion ' || {Log.IdColumn}
                                   WHEN 2 THEN 'resync started'
                                   WHEN 3 THEN 'resync finished'
                                   ELSE 'type ' || event_type
                               END
                        FROM {Log.Events}
                        ORDER BY id
                        """);
    }

    /// <summary>
    /// Columns of one row of the derived table, or expressions over them, as text in the order asked.
    /// </summary>
    protected List<string> Values(long id, params string[] columns)
    {
        return Strings($"""
                        SELECT value::text
                        FROM {Log.State}, unnest(ARRAY[{string.Join(", ", columns)}]) WITH ORDINALITY AS row_values(value, n)
                        WHERE {Log.IdColumn} = {id}
                        ORDER BY n
                        """);
    }

    protected string Value(long id, string column)
    {
        return Values(id, column).Single();
    }

    protected bool ResyncInProgress(long sender = Sender)
    {
        return Scalar<bool>($"SELECT resync_in_progress FROM {Log.ResyncView} WHERE sender_id = {sender}");
    }

    /// <summary>
    /// One sender's row of the resync view.
    /// </summary>
    protected sealed record ResyncStatus(long Sender, long? StartMarker, DateTime? Started, DateTime? Finished, bool InProgress);

    protected List<ResyncStatus> ResyncStatuses()
    {
        return Column(Owner,
            $"""
             SELECT sender_id, resync_started_event_row_id, time_resync_started, time_resync_finished, resync_in_progress
             FROM {Log.ResyncView}
             ORDER BY sender_id
             """,
            reader => new ResyncStatus(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.GetBoolean(4)));
    }

    /// <summary>
    /// When an event reached the log: the start of the transaction its batch arrived in.
    /// </summary>
    protected DateTime ArrivalOf(long eventId)
    {
        return Scalar<DateTime>($"SELECT time_inserted FROM {Log.Events} WHERE id = {eventId}");
    }

    /// <summary>
    /// Every column in which a derived row differs from the event it was written from, as <c>id: column</c>.
    /// </summary>
    protected List<string> Mismatches()
    {
        return Strings($"""
                        SELECT s.{Log.IdColumn} || ': ' || key
                        FROM {Log.State} s
                                 JOIN {Log.Events} e ON e.id = s.source_event_row_id
                                 JOIN LATERAL jsonb_each(to_jsonb(e) - 'id' - 'time_inserted' - 'event_type') ON true
                        WHERE value IS DISTINCT FROM ((to_jsonb(s) - 'source_event_row_id' - 'time_applied') -> key)
                        ORDER BY s.{Log.IdColumn}, key
                        """);
    }

    protected long Count(string table)
    {
        return Scalar<long>($"SELECT count(*) FROM {table}");
    }

    /// <summary>
    /// Counts the rows a connection sees, inside whatever transaction it has open.
    /// </summary>
    protected static long Count(NpgsqlConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";

        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Inserts one row into a table the logs do not cover, zeroed but for the columns given as SQL literals.
    /// </summary>
    protected void InsertRow(string table, params (string Column, string Literal)[] values)
    {
        var columns = ColumnsOf(table);

        var literals = columns.Select(column => values.FirstOrDefault(value => value.Column == column.Name).Literal ?? column.Type switch
        {
            "character varying" => "''",
            "timestamp with time zone" => "now()",
            _ => "0"
        });

        Execute($"INSERT INTO {table} ({string.Join(", ", columns.Select(column => column.Name))}) VALUES ({string.Join(", ", literals)})");
    }

    protected void DropTrigger()
    {
        Execute($"DROP TRIGGER {Log.Trigger} ON {Log.Events}");
    }

    // ---------------------------------------------------------------------------------------------------
    // The catalog
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A table's or a view's columns in order, as <c>name type</c>, plus <c>null</c> for a table column that takes NULL.
    /// </summary>
    protected List<string> ColumnDefinitions(string relation)
    {
        return Strings($"""
                        SELECT a.attname || ' ' || format_type(a.atttypid, a.atttypmod)
                               || CASE WHEN a.attnotnull OR c.relkind = 'v' THEN '' ELSE ' null' END
                        FROM pg_attribute a
                                 JOIN pg_class c ON c.oid = a.attrelid
                        WHERE a.attrelid = '{relation}'::regclass
                          AND a.attnum > 0
                          AND NOT a.attisdropped
                        ORDER BY a.attnum
                        """);
    }

    protected List<string> TriggerDefinitions(string table)
    {
        return Strings($"SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgrelid = '{table}'::regclass AND NOT tgisinternal ORDER BY tgname");
    }

    /// <summary>
    /// A table's indexes as PostgreSQL prints their definitions, in ordinal order.
    /// </summary>
    protected List<string> IndexDefinitions(string table)
    {
        return [.. Strings($"SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{table}'").Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A table's column defaults as <c>column = expression</c>, in ordinal order.
    /// </summary>
    protected List<string> Defaults(string table)
    {
        return
        [
            .. Strings($"""
                        SELECT a.attname || ' = ' || pg_get_expr(d.adbin, d.adrelid)
                        FROM pg_attrdef d
                                 JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                        WHERE d.adrelid = '{table}'::regclass
                        """).Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// The plan PostgreSQL would run the query with.
    /// </summary>
    protected string Plan(string sql)
    {
        return string.Join(Environment.NewLine, Strings($"EXPLAIN {sql}"));
    }

    /// <summary>
    /// A table's columns in order, minus the id and time_inserted the table fills itself.
    /// </summary>
    protected List<LogColumn> ColumnsOf(string table)
    {
        if (columnsByTable.TryGetValue(table, out var known))
        {
            return known;
        }

        using var connection = Open(Owner);
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT column_name, data_type, is_nullable = 'YES', character_maximum_length
                              FROM information_schema.columns
                              WHERE table_schema = 'public'
                                AND table_name = @table
                                AND column_name NOT IN ('id', 'time_inserted')
                              ORDER BY ordinal_position
                              """;
        command.Parameters.AddWithValue("table", table);

        using var reader = command.ExecuteReader();

        var columns = new List<LogColumn>();

        while (reader.Read())
        {
            columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return columnsByTable[table] = columns;
    }

    // ---------------------------------------------------------------------------------------------------
    // SQL
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the action and asserts PostgreSQL refused it with the given SQLSTATE.
    /// </summary>
    protected static void AssertRefused(string sqlState, Action action, string? what = null)
    {
        var exception = Record.Exception(action);

        Assert.True(exception is PostgresException postgres && postgres.SqlState == sqlState,
            $"Expected SQLSTATE {sqlState}{(what is null ? "" : $" for {what}")}, got: {exception?.Message ?? "no error"}");
    }

    /// <summary>
    /// Asserts the account may not run the statement.
    /// </summary>
    protected static void AssertDenied(string account, string sql)
    {
        AssertRefused(PostgresErrorCodes.InsufficientPrivilege, () => Execute(account, sql), sql);
    }

    protected void Execute(string sql)
    {
        Execute(Owner, sql);
    }

    protected static void Execute(string connectionString, string sql)
    {
        using var connection = Open(connectionString);
        Execute(connection, sql);
    }

    protected static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    protected T Scalar<T>(string sql)
    {
        return Scalar<T>(Owner, sql);
    }

    protected static T Scalar<T>(string connectionString, string sql)
    {
        using var connection = Open(connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    protected List<string> Strings(string sql)
    {
        return Column(Owner, sql, reader => reader.GetString(0));
    }

    protected List<long> Longs(string sql)
    {
        return Column(Owner, sql, reader => reader.GetInt64(0));
    }

    protected static NpgsqlConnection Open(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        return connection;
    }

    private static List<T> Column<T>(string connectionString, string sql, Func<DbDataReader, T> read)
    {
        using var connection = Open(connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        var values = new List<T>();

        while (reader.Read())
        {
            values.Add(read(reader));
        }

        return values;
    }

    private string WithUser(DatabaseUser user)
    {
        return new NpgsqlConnectionStringBuilder(Owner) { Username = user.User, Password = user.Password }.ConnectionString;
    }

    private static string WithDatabase(string connectionString, string databaseName)
    {
        return new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
    }

    private DbMigratorRequest CreateRequest(string[] variables)
    {
        var configured = configBuilder.PostgresConnectionString;

        Assert.False(string.IsNullOrWhiteSpace(configured), "No PostgreSQL connection string configured. Start docker-compose.yml or set Postgres__ConnectionString.");

        var databaseName = $"event_logs_{Guid.NewGuid():N}"[..40];

        provisionedDatabases.Add((configured!, databaseName));

        return new(
            DatabaseProvider.Postgres,
            WithDatabase(configured!, databaseName),
            databaseName,
            "schema_versions",
            [new(true, "psql")],
            configBuilder.RepositoryRoot,
            Variables(databaseName, variables),
            false,
            false,
            null,
            [
                new() { Access = "exporter", User = $"{databaseName}_exporter", Password = "passw0rd" },
                new() { Access = "ro", User = $"{databaseName}_reader", Password = "passw0rd" }
            ]);
    }
}
