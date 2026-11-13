using System.Text;
using WinBackground;

namespace DocFiler.Tests;

/// <summary>The after-run hook, run through WinBackground's Shell.Run: the report reaches the hook byte for byte as
/// earlier builds wrote it, and the hook's output and exit code reach the log.</summary>
[Collection("Serial")]
public sealed class HookTests
{
    private static string[] PowerShell(string script) =>
        [Shell.PowerShellExe, "-NoProfile", "-NonInteractive", "-Command", script];

    private static ProcessResult Result()
    {
        var result = new ProcessResult { Converted = 1, Copied = 1, Skipped = 5 };
        result.Written.Add("meetings/catch-ups/zoë-smith/2026-11-13.md");
        result.Moved.Add(new MovedFile("notes/2026-10-01-prep.md", "notes/archive/2026-10/2026-10-01-prep.md"));
        result.FailedFiles.Add(@"C:\Staging\Notes\Broken 2026-11-13.docx");
        return result;
    }

    [Fact]
    public void The_report_reaches_the_hook_byte_for_byte_and_its_output_is_logged()
    {
        using var box = new Sandbox();
        var received = Path.Combine(box.Root, "received.json");
        box.WriteConfig(60, afterRun: PowerShell(
            $"$in = [Console]::OpenStandardInput(); $out = [IO.File]::Create('{received}'); $in.CopyTo($out); $out.Close(); " +
            "[Console]::Out.Write('first line' + [char]13 + [char]10 + 'second line' + [char]10); [Console]::Error.Write('a warning'); exit 3"));
        using var log = new LogCapture(box.Root);
        var config = FilerConfig.Load(box.ConfigPath);

        AfterRunHook.Run(config, Result(), [@"C:\Staging\Notes\Stray.docx"]);

        // What an earlier build wrote: the indented report (CRLF on Windows), anything outside ASCII as \uXXXX.
        var report = Json.WriteReport(new HookReport(config.ConfigPath, config.ResolvedRepoRoot,
            ["meetings/catch-ups/zoë-smith/2026-11-13.md"],
            [new HookMove("notes/2026-10-01-prep.md", "notes/archive/2026-10/2026-10-01-prep.md")],
            [@"C:\Staging\Notes\Broken 2026-11-13.docx"], [@"C:\Staging\Notes\Stray.docx"],
            "1 converted, 1 copied, 5 unchanged, 1 failed, 1 archived"));
        Assert.Contains("\r\n", report);
        Assert.Contains(@"zo\u00EB-smith", report);
        Assert.Equal(Encoding.ASCII.GetBytes(report), File.ReadAllBytes(received));

        var lines = log.Lines.Select(line => line[20..]).ToList();   // without the timestamp
        Assert.Equal(
        [
            $"After-run hook: {string.Join(' ', config.AfterRun)}",
            "  hook: first line",
            "  hook: second line",
            "  hook: a warning",
            "ERROR: After-run hook exited 3",
        ], lines);
    }

    [Fact]
    public void Failures_are_left_out_while_a_retry_is_still_to_come()
    {
        using var box = new Sandbox();
        var received = Path.Combine(box.Root, "received.json");
        box.WriteConfig(60, afterRun: PowerShell(
            $"$in = [Console]::OpenStandardInput(); $out = [IO.File]::Create('{received}'); $in.CopyTo($out); $out.Close()"));
        using var log = new LogCapture(box.Root);

        AfterRunHook.Run(FilerConfig.Load(box.ConfigPath), Result(), [], reportFailures: false);

        Assert.Contains("\"failed\": []", File.ReadAllText(received));
        Assert.DoesNotContain(log.Lines, line => line.Contains("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hook_that_exits_without_reading_its_input_is_no_error()
    {
        using var box = new Sandbox();
        box.WriteConfig(60, afterRun: [Shell.SystemProgram("cmd.exe"), "/c", "exit 0"]);
        using var log = new LogCapture(box.Root);

        AfterRunHook.Run(FilerConfig.Load(box.ConfigPath), Result(), []);

        Assert.DoesNotContain(log.Lines, line => line.Contains("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hook_that_cant_start_is_logged()
    {
        using var box = new Sandbox();
        box.WriteConfig(60, afterRun: [Path.Combine(box.Root, "no-such-hook.exe")]);
        using var log = new LogCapture(box.Root);

        AfterRunHook.Run(FilerConfig.Load(box.ConfigPath), Result(), []);

        Assert.Contains(log.Lines, line => line.Contains(" ERROR: After-run hook failed: ", StringComparison.Ordinal));
    }

    [Fact]
    public void No_hook_configured_runs_nothing()
    {
        using var box = new Sandbox();
        using var log = new LogCapture(box.Root);
        AfterRunHook.Run(FilerConfig.Load(box.ConfigPath), Result(), []);
        Assert.Empty(log.Lines);
    }
}
