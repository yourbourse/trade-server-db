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

using Xunit.Abstractions;

namespace YB.TradeServer.Db.Migrator.Tests.Execution.EventLogs;

/// <summary>
/// Each derived table against a replay of its log in C#, over random histories: the trigger after every batch, and the
/// reconcile after the trigger went missing partway.
/// </summary>
/// <remarks>
/// The histories are seeded, so a failure names a seed that reproduces it. Two senders and six ids make ids recur,
/// resyncs overlap and markers share batches far more often than the hand-written cases can.
/// </remarks>
[Trait("Category", "Integration")]
public class EventLogReplayIntegrationTests(ConfigBuilder configBuilder, ITestOutputHelper output) : EventLogTestBase(configBuilder, output)
{
    private const int Batches = 30;

    public static TheoryData<EventLog, int> RandomHistories()
    {
        var histories = new TheoryData<EventLog, int>();

        foreach (var log in Enum.GetValues<EventLog>())
        {
            foreach (var seed in new[] { 1, 2, 3 })
            {
                histories.Add(log, seed);
            }
        }

        return histories;
    }

    [Theory(DisplayName = "Invariant Should hold what a replay of the log holds, after every batch of a random history")]
    [MemberData(nameof(RandomHistories))]
    public void Invariant_Should_hold_what_a_replay_of_the_log_holds_after_every_batch_of_a_random_history(EventLog log, int seed)
    {
        // arrange
        Migrate(log);
        var replay = new Replay(Log);
        var history = RandomHistory(seed);

        // act, assert
        for (var batch = 0; batch < history.Count; batch++)
        {
            replay.Apply(Export(history[batch]), history[batch]);

            AssertSameRows(replay.Rows(), RowsWithStamps(), $"seed {seed}, after batch {batch + 1}");
        }
    }

    /// <summary>
    /// The reconcile replays only the newest event per entity and the newest finished resync, yet has to reach the same table.
    /// </summary>
    [Theory(DisplayName = "Migration Should rebuild what a replay of the log holds when the trigger went missing partway")]
    [MemberData(nameof(RandomHistories))]
    public void Migration_Should_rebuild_what_a_replay_of_the_log_holds_when_the_trigger_went_missing_partway(EventLog log, int seed)
    {
        // arrange
        Migrate(log);
        var replay = new Replay(Log);
        var history = RandomHistory(seed);
        var lostAt = seed * 7 % Batches;

        // act, the trigger is dropped at a batch the seed picks, and the export carries on without it
        for (var batch = 0; batch < history.Count; batch++)
        {
            if (batch == lostAt)
            {
                DropTrigger();
            }

            replay.Apply(Export(history[batch]), history[batch]);
        }

        MigrateAgain(force: true);

        // assert
        AssertSameRows(replay.Rows(), RowsWithStamps(), $"seed {seed}, trigger lost at batch {lostAt + 1}");
    }

    /// <summary>
    /// Thirty batches of one to five events over two senders and six ids, the same for the same seed.
    /// </summary>
    private List<LogEvent[]> RandomHistory(int seed)
    {
        var random = new Random(seed);

        return
        [
            .. Enumerable.Range(1, Batches).Select(batch =>
                Enumerable.Range(1, random.Next(1, 6)).Select(index => RandomEvent(random, $"b{batch}e{index}")).ToArray())
        ];
    }

    private LogEvent RandomEvent(Random random, string label)
    {
        var sender = random.Next(2) == 0 ? Sender : OtherSender;
        var id = random.Next(1, 7);

        return random.Next(20) switch
        {
            < 11 => State(id, version: random.Next(1, 10), label: label, sender: sender),
            < 14 => Deleted(id, sender),
            < 16 => ResyncStarted(sender),
            < 19 => ResyncFinished(sender),
            _ => Event(99, sender).With(Log.IdColumn, (long)id)
        };
    }

    private static void AssertSameRows(List<string> expected, List<string> actual, string when)
    {
        Assert.True(expected.SequenceEqual(actual),
            $"{when}{Environment.NewLine}replay: {string.Join(", ", expected)}{Environment.NewLine}table:  {string.Join(", ", actual)}");
    }

    /// <summary>
    /// The projection's contract in a few lines of C#: what the table holds once the log has been applied in id order.
    /// </summary>
    private sealed class Replay(EventLogTables log)
    {
        private readonly SortedDictionary<(long Sender, long Id), (long Version, string Label, long Stamp)> table = [];
        private readonly List<(long Sender, long Id)> startMarkers = [];

        public void Apply(List<long> ids, LogEvent[] batch)
        {
            foreach (var (id, logEvent) in ids.Zip(batch))
            {
                Apply(id, logEvent);
            }
        }

        public List<string> Rows()
        {
            return
            [
                .. table.Select(row =>
                    $"{row.Key.Sender}/{row.Key.Id} v{row.Value.Version}{(row.Value.Label == "" ? "" : $" {row.Value.Label}")} #{row.Value.Stamp}")
            ];
        }

        private void Apply(long id, LogEvent logEvent)
        {
            var key = (logEvent.Sender, EntityId(logEvent));

            switch (logEvent.EventType)
            {
                case EventType.State:
                    table[key] = ((long)logEvent.Values["version"]!, (string)logEvent.Values[log.LabelColumn]!, id);
                    break;

                case EventType.Deletion:
                    table.Remove(key);
                    break;

                case EventType.ResyncStarted:
                    startMarkers.Add((logEvent.Sender, id));
                    break;

                // sweep against the sender's newest start marker before this one; with none, nothing is older
                case EventType.ResyncFinished:
                    var watermark = startMarkers.Where(marker => marker.Sender == logEvent.Sender && marker.Id < id).Select(marker => (long?)marker.Id).Max();

                    foreach (var swept in table.Where(row => row.Key.Sender == logEvent.Sender && row.Value.Stamp < watermark).Select(row => row.Key).ToList())
                    {
                        table.Remove(swept);
                    }

                    break;
            }
        }

        private long EntityId(LogEvent logEvent)
        {
            return logEvent.Values.TryGetValue(log.IdColumn, out var id) ? (long)id! : 0;
        }
    }
}
