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
using System.Reflection;
using Xunit.Sdk;

namespace YB.TradeServer.Db.Migrator.Tests.Execution.EventLogs;

/// <summary>
/// The four event logs the trade server exports, each with the table and the view derived from it.
/// </summary>
public enum EventLog
{
    TradingAccounts,
    Clients,
    Symbols,
    Groups
}

/// <summary>
/// The event types the trade server writes.
/// </summary>
public static class EventType
{
    public const int State = 0;
    public const int Deletion = 1;
    public const int ResyncStarted = 2;
    public const int ResyncFinished = 3;
}

/// <summary>
/// One event as the exporter writes it: who sent it, its type and the columns it sets.
/// </summary>
/// <remarks>
/// Every column it does not set carries the exporter's zero for its type, as on the real export.
/// </remarks>
public sealed record LogEvent(long Sender, int EventType, ImmutableDictionary<string, object?> Values)
{
    public LogEvent With(string column, object? value)
    {
        return this with { Values = Values.SetItem(column, value) };
    }
}

/// <summary>
/// A column of an event log, as information_schema describes it.
/// </summary>
public sealed record LogColumn(string Name, string Type, bool Nullable, int? Width);

/// <summary>
/// The objects one event log is made of, under the names the migration gave them.
/// </summary>
/// <param name="Entity">What a row is about, as the history index names it.</param>
/// <param name="LabelColumn">A text column the suites tell rows apart by.</param>
public sealed record EventLogTables(
    string Events,
    string State,
    string ResyncView,
    string IdColumn,
    string LabelColumn,
    string Entity,
    string ProjectionScript)
{
    public string HistoryIndex => $"idx_{Entity}_row_{Events}";

    public string MarkerIndex => $"idx_markers_{Events}";

    public string Trigger => $"trg_project_{State}";

    public string Function => $"project_{State}";

    public static EventLogTables Of(EventLog log)
    {
        return log switch
        {
            EventLog.TradingAccounts => new("trading_account_events", "trading_accounts", "trading_accounts_resync",
                "trading_account_id", "comment", "account", "trading_accounts_projection.sql"),
            EventLog.Clients => new("client_events", "clients", "clients_resync",
                "client_id", "comment", "client", "clients_projection.sql"),
            EventLog.Symbols => new("symbol_events", "symbols", "symbols_resync",
                "symbol_id", "description", "symbol", "symbols_projection.sql"),
            EventLog.Groups => new("group_events", "groups", "groups_resync",
                "group_id", "company", "group", "groups_projection.sql"),
            _ => throw new ArgumentOutOfRangeException(nameof(log), log, null)
        };
    }

    /// <summary>
    /// The same log under names a deployment chose, and the --variables that choose them.
    /// </summary>
    public (EventLogTables Tables, string[] Variables) Renamed()
    {
        var renamed = this with { Events = $"{Entity}_log", State = $"{Entity}_state", ResyncView = $"{Entity}_resync" };

        return (renamed, [$"{Events}_table={renamed.Events}", $"{State}_table={renamed.State}", $"{ResyncView}_view={renamed.ResyncView}"]);
    }
}

/// <summary>
/// Runs a theory once against each event log.
/// </summary>
public sealed class EveryEventLogAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
    {
        return Enum.GetValues<EventLog>().Select(log => new object[] { log });
    }
}
