using System.Diagnostics;

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

        var startInfo = new ProcessStartInfo(config.AfterRun[0])
        {
            WorkingDirectory = config.ResolvedRepoRoot,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in config.AfterRun.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Log.Info($"After-run hook: {string.Join(' ', config.AfterRun)}");

        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("the process did not start");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(report);
            process.StandardInput.Close();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                process.Kill(true);
                Log.Error($"After-run hook did not finish within {Timeout.TotalMinutes} minutes, killed");
                return;
            }

            foreach (var line in (output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult())
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Log.Info($"  hook: {line}");
            }

            if (process.ExitCode != 0)
            {
                Log.Error($"After-run hook exited {process.ExitCode}");
            }
        }
        catch (Exception exception)
        {
            Log.Error($"After-run hook failed: {exception.Message}");
        }
    }
}
