namespace DocFiler;

/// <summary>One locked run: convert, copy, then the after-run hook if there is anything to report.</summary>
public static class Filer
{
    public const int LockTimedOut = 3;

    public static int Run(FilerConfig config, bool dryRun, bool hook, IReadOnlyList<string> newUnmatched, TimeSpan lockWait)
    {
        using var runLock = new RunLock(config.ConfigPath);
        if (!runLock.Acquire(lockWait))
        {
            Log.Error($"Another doc-filer run is still going for {config.ConfigPath}; gave up waiting after {lockWait.TotalMinutes:0} minutes.");
            return LockTimedOut;
        }

        var result = new DirectoryProcessor(config, dryRun).Run();
        Log.Info($"Processing complete: {result}");

        if (hook && !dryRun && (result.Written.Count > 0 || result.Failed > 0 || newUnmatched.Count > 0))
        {
            AfterRunHook.Run(config, result, newUnmatched);
        }

        return result.Failed > 0 ? 1 : 0;
    }
}
