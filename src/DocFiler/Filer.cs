namespace DocFiler;

/// <summary>
/// One locked run: convert, copy, then the after-run hook if there is anything to report, then the
/// config's git commits (<see cref="RepoCommits"/>) if anything was filed or archived. The hook goes
/// first so that a hook still looking at what git sees as changed (new transcripts, say) sees the
/// files before they're committed. The watcher passes <c>reportFailures: false</c> when it will try
/// a failed file again, so a file caught mid-sync isn't reported until the retries are used up.
/// What was filed and archived goes in the shared activity journal (<see cref="Activity"/>), by hand for a run from the
/// command line, auto for the watcher's.
/// </summary>
public static class Filer
{
    public const int LockTimedOut = 3;

    public static int Run(FilerConfig config, bool dryRun, bool hook, IReadOnlyList<string> newUnmatched, TimeSpan lockWait,
        bool reportFailures = true, Action<string, IReadOnlyList<string>>? notify = null, string by = ActivityJournal.By.Hand)
    {
        using var runLock = new RunLock(config.ConfigPath);
        if (!runLock.Acquire(lockWait))
        {
            Log.Error($"Another doc-filer run is still going for {config.ConfigPath}; gave up waiting after {lockWait.TotalMinutes:0} minutes.");
            return LockTimedOut;
        }

        var result = new DirectoryProcessor(config, dryRun).Run();
        Log.Info($"Processing complete: {result}");
        if (!dryRun)
        {
            Activity.Record(config, result, by);
        }

        if (hook && !dryRun && (result.Written.Count > 0 || result.Moved.Count > 0 || (reportFailures && result.Failed > 0) || newUnmatched.Count > 0))
        {
            AfterRunHook.Run(config, result, newUnmatched, reportFailures);
        }

        if (hook && !dryRun)
        {
            Problems.Report(config, newUnmatched, reportFailures ? result.FailedFiles : [], notify ?? WinBackground.Toast.Notify);
        }

        if (hook && !dryRun)
        {
            RepoCommits.Run(config, result, notify ?? WinBackground.Toast.Notify);
        }

        return result.Failed > 0 ? 1 : 0;
    }
}
