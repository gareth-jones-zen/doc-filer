namespace DocFiler;

/// <summary>
/// One locked run: convert, copy, then the after-run hook if there is anything to report.
/// The watcher passes <c>reportFailures: false</c> when it will try a failed file again, so a
/// file caught mid-sync isn't reported until the retries are used up.
/// </summary>
public static class Filer
{
    public const int LockTimedOut = 3;

    public static int Run(FilerConfig config, bool dryRun, bool hook, IReadOnlyList<string> newUnmatched, TimeSpan lockWait,
        bool reportFailures = true)
    {
        using var runLock = new RunLock(config.ConfigPath);
        if (!runLock.Acquire(lockWait))
        {
            Log.Error($"Another doc-filer run is still going for {config.ConfigPath}; gave up waiting after {lockWait.TotalMinutes:0} minutes.");
            return LockTimedOut;
        }

        var result = new DirectoryProcessor(config, dryRun).Run();
        Log.Info($"Processing complete: {result}");

        if (hook && !dryRun && (result.Written.Count > 0 || result.Moved.Count > 0 || (reportFailures && result.Failed > 0) || newUnmatched.Count > 0))
        {
            AfterRunHook.Run(config, result, newUnmatched, reportFailures);
        }

        return result.Failed > 0 ? 1 : 0;
    }
}
