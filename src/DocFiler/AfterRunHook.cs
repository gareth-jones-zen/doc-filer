using WinBackground;

namespace DocFiler;

/// <summary>
/// Hands what a run did to the target repo's own command, which does whatever that repo
/// needs (commit, sweep, queue, notify). The tool itself knows nothing about git.
///
/// The report goes in as JSON on stdin; see README.md, "The after-run hook".
/// </summary>
public static class AfterRunHook
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public static void Run(FilerConfig config, ProcessResult result, IReadOnlyList<string> unmatched,
        bool reportFailures = true)
    {
        if (config.AfterRun.Count == 0)
        {
            return;
        }

        var report = Json.WriteReport(new HookReport(
            Config: config.ConfigPath,
            RepoRoot: config.ResolvedRepoRoot,
            Written: result.Written,
            Moved: [.. result.Moved.Select(move => new HookMove(move.From, move.To))],
            Failed: reportFailures ? result.FailedFiles : [],
            Unmatched: unmatched,
            Summary: result.ToString()));

        Log.Info($"After-run hook: {string.Join(' ', config.AfterRun)}");

        try
        {
            // WinBackground's Shell.Run: no window, the report on stdin (then closed), output read as UTF-8, and the
            // hook killed with everything it started if it runs too long. It writes stdin in text mode (each LF as
            // CRLF), and the report is already CRLF (an indented JSON writer on Windows), so it goes in as LF to
            // arrive byte for byte as before.
            var done = Shell.Run(config.AfterRun[0], config.AfterRun.Skip(1), Timeout,
                stdin: report.Replace("\r\n", "\n"), workingDirectory: config.ResolvedRepoRoot, utf8: true);

            foreach (var line in (done.StandardOutput + done.StandardError)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Log.Info($"  hook: {line}");
            }

            if (done.ExitCode != 0)
            {
                Log.Error($"After-run hook exited {done.ExitCode}");
            }
        }
        catch (ShellTimeoutException)
        {
            Log.Error($"After-run hook did not finish within {Timeout.TotalMinutes} minutes, killed");
        }
        catch (Exception exception)
        {
            Log.Error($"After-run hook failed: {exception.Message}");
        }
    }
}
