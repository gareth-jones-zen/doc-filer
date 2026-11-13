using WinBackground;

namespace DocFiler.Tests;

/// <summary>Watch mode's timing, on a fake clock: the settle wait, the retries, the hourly alive line, the new-day
/// run, config reloads, unmatched files reported once, and one watcher per config.</summary>
[Collection("Serial")]
public sealed class WatcherTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 10, 0, 0);

    private sealed class Stop : Exception;

    private sealed record Call(DateTime At, IReadOnlyList<string> Fresh, bool ReportFailures);

    /// <summary>Runs a watcher for a number of 30-second passes on a fake clock. onPass runs before each pass's
    /// checks (after its sleep), with the pass number from 1.</summary>
    private static List<Call> Watch(Sandbox box, int passes, Func<int>? result = null, Action<int, DateTime>? onPass = null,
        DateTime? start = null)
    {
        var now = start ?? Start;
        var count = 0;
        var calls = new List<Call>();
        var watcher = new Watcher(box.ConfigPath)
        {
            Now = () => now,
            Sleep = wait =>
            {
                Assert.Equal(TimeSpan.FromSeconds(30), wait);
                if (++count > passes)
                {
                    throw new Stop();
                }

                now += wait;
                onPass?.Invoke(count, now);
            },
            RunFiler = (_, fresh, report) =>
            {
                calls.Add(new Call(now, [.. fresh], report));
                return result?.Invoke() ?? 0;
            },
            StateFolder = box.State,
        };
        var clock = Log.Clock;
        Log.Clock = () => now;
        try
        {
            Assert.Throws<Stop>(() => watcher.Run());
        }
        finally
        {
            Log.Clock = clock;
        }
        return calls;
    }

    [Fact]
    public void Runs_once_at_start_then_only_after_the_folders_settle()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        var calls = Watch(box, passes: 10, onPass: (pass, _) =>
        {
            if (pass == 2)
            {
                box.AddStaging("Note 2026-11-13.txt");
            }
        });

        // Seen at pass 2 (10:01:00); settle is 60 seconds, so the run is at the first pass a minute later.
        Assert.Equal([Start, Start.AddSeconds(120)], calls.Select(call => call.At));
        Assert.All(calls, call => Assert.True(call.ReportFailures is false));   // the first try of a run never reports a failure
    }

    [Fact]
    public void A_change_during_the_settle_restarts_it()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        var calls = Watch(box, passes: 12, onPass: (pass, _) =>
        {
            if (pass is 2 or 3)
            {
                box.AddStaging("Note 2026-11-13.txt", new string((char)('a' + pass), pass));   // a new length: a fast second write can keep the same modified time
            }
        });

        Assert.Equal([Start, Start.AddSeconds(150)], calls.Select(call => call.At));
    }

    [Fact]
    public void A_failed_file_is_retried_three_times_five_minutes_apart_and_only_the_last_reports_it()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        var calls = Watch(box, passes: 80, result: () => 1);

        Assert.Equal([Start, Start.AddMinutes(5), Start.AddMinutes(10), Start.AddMinutes(15)], calls.Select(call => call.At));
        Assert.Equal([false, false, false, true], calls.Select(call => call.ReportFailures));
        Assert.Contains(log.Lines, line => line.EndsWith(" WARNING: Trying the failed file(s) again at 10:05 (retry 1 of 3)", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.EndsWith(" WARNING: Trying the failed file(s) again at 10:15 (retry 3 of 3)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_retry_that_works_ends_the_retries()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        var results = new Queue<int>([1, 0]);
        var calls = Watch(box, passes: 60, result: () => results.Count > 0 ? results.Dequeue() : 0);

        Assert.Equal([Start, Start.AddMinutes(5)], calls.Select(call => call.At));
    }

    [Fact]
    public void Logs_alive_once_an_hour()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        Watch(box, passes: 2 * 120 + 1);

        var alive = log.Lines.Where(line => line.EndsWith(" alive", StringComparison.Ordinal)).ToList();
        Assert.Equal(["2026/11/13 11:00:00 alive", "2026/11/13 12:00:00 alive"], alive);
    }

    [Fact]
    public void A_new_day_runs_the_archive_rules_once_the_folders_settle()
    {
        using var box = new Sandbox(archive: """{ "folder": "notes", "keepDays": 14, "to": "archive/{yyyy}-{mm}" }""");
        using var log = new LogCapture(box.Root);
        var lateEvening = new DateTime(2026, 11, 13, 23, 58, 0);
        var calls = Watch(box, passes: 10, start: lateEvening);

        Assert.Equal([lateEvening, new DateTime(2026, 11, 14, 0, 1, 0)], calls.Select(call => call.At));
        Assert.Contains("2026/11/14 00:00:00 New day: running the archive rules once the folders settle", log.Lines);
    }

    [Fact]
    public void Without_archive_rules_a_new_day_runs_nothing()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        var calls = Watch(box, passes: 10, start: new DateTime(2026, 11, 13, 23, 58, 0));

        Assert.Single(calls);
    }

    [Fact]
    public void A_config_edit_is_reloaded_and_a_broken_one_keeps_the_old_config()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        Watch(box, passes: 6, onPass: (pass, now) =>
        {
            if (pass == 2)
            {
                box.WriteConfig(settleSeconds: 90);
                File.SetLastWriteTimeUtc(box.ConfigPath, DateTime.UtcNow.AddMinutes(1));
            }
            if (pass == 4)
            {
                File.WriteAllText(box.ConfigPath, "{ not json");
                File.SetLastWriteTimeUtc(box.ConfigPath, DateTime.UtcNow.AddMinutes(2));
            }
        });

        Assert.Contains("2026/11/13 10:01:00 Config changed; reloaded", log.Lines);
        Assert.Contains(log.Lines, line => line.StartsWith("2026/11/13 10:02:00 ERROR: Config changed but didn't load, so the old one stays in use: ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unmatched_file_is_reported_once_even_across_restarts()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        var stray = box.AddStaging("Stray name.txt");

        var first = Watch(box, passes: 6, onPass: (pass, _) =>
        {
            if (pass == 1)
            {
                box.AddStaging("Note 2026-11-13.txt");
            }
        });
        Assert.Equal(2, first.Count);
        Assert.Equal([stray], first[0].Fresh);
        Assert.Empty(first[1].Fresh);
        Assert.Single(log.Lines, line => line.EndsWith($" WARNING: No rule matches {stray}", StringComparison.Ordinal));
        Assert.Equal([stray], File.ReadAllLines(Path.Combine(box.State, $"unmatched-{RunLock.Key(box.ConfigPath)}.txt")));

        // A restart (every logon) doesn't report it again.
        var second = Watch(box, passes: 1);
        Assert.Empty(second[0].Fresh);

        // Gone, then saved again under the same name: reported again.
        File.Delete(stray);
        Watch(box, passes: 1);
        box.AddStaging("Stray name.txt");
        var third = Watch(box, passes: 1);
        Assert.Equal([stray], third[0].Fresh);
    }

    [Fact]
    public void A_second_watcher_on_the_same_config_exits_with_4_and_files_nothing()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        using var first = Locks.SingleInstance(RunLock.WatchName(box.ConfigPath));
        Assert.NotNull(first);

        var ran = false;
        var watcher = new Watcher(box.ConfigPath) { RunFiler = (_, _, _) => { ran = true; return 0; }, StateFolder = box.State };

        Assert.Equal(Watcher.AlreadyRunning, watcher.Run());
        Assert.False(ran);
        Assert.Contains(log.Lines, line => line.Contains(" WARNING: Another doc-filer watcher is already running for ", StringComparison.Ordinal));
    }
}
