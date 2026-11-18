using System.Text.Json.Nodes;
using WinBackground;

namespace DocFiler.Tests;

/// <summary>1.4.0: several after-run commands, and the tool raising its own problems (notify, [review]).</summary>
[Collection("Serial")]
public sealed class ProblemsTests
{
    private static string Settings(Sandbox box, string extra) => File.ReadAllText(box.ConfigPath).Replace("\"repoRoot\": \"repo\",", "\"repoRoot\": \"repo\",\n" + extra);

    [Fact]
    public void Each_after_run_command_gets_the_report_in_turn()
    {
        using var box = new Sandbox();
        var a = Path.Combine(box.Root, "a.txt").Replace("\\", "\\\\");
        var b = Path.Combine(box.Root, "b.txt").Replace("\\", "\\\\");
        string Command(string file) =>
            $"[\"{Shell.PowerShellExe.Replace("\\", "\\\\")}\", \"-NoProfile\", \"-NonInteractive\", \"-Command\", \"[Console]::In.ReadToEnd() | Set-Content '{file}'\"]";
        File.WriteAllText(box.ConfigPath, Settings(box, $"\"afterRun\": [{Command(a)}, {Command(b)}],").Replace("\"afterRun\": [],", ""));
        var config = FilerConfig.Load(box.ConfigPath);
        Assert.Equal(2, config.AfterRunCommands.Count);

        var result = new ProcessResult { Copied = 1 };
        result.Written.Add("out/2026-11-13.md");
        AfterRunHook.Run(config, result, []);
        foreach (var file in new[] { a, b })
            Assert.Contains("out/2026-11-13.md", File.ReadAllText(file.Replace("\\\\", "\\")));
    }

    [Fact]
    public void A_bad_after_run_is_a_settings_problem()
    {
        using var box = new Sandbox();
        File.WriteAllText(box.ConfigPath, Settings(box, "\"afterRun\": [[\"x\"], \"y\"],").Replace("\"afterRun\": [],", ""));
        Assert.Contains("afterRun should be", Assert.ThrowsAny<Exception>(() => FilerConfig.Load(box.ConfigPath)).Message);
    }

    [Fact]
    public void Unmatched_and_failed_files_are_notified_and_unmatched_ones_raised()
    {
        using var box = new Sandbox();
        var queue = Path.Combine(box.Root, "pending.json").Replace("\\", "\\\\");
        File.WriteAllText(box.ConfigPath, Settings(box, $"\"notify\": true,\n\"review\": {{ \"file\": \"{queue}\", \"source\": \"transcripts\", \"lock\": \"doc-filer-tests-{Guid.NewGuid():N}\" }},"));
        var config = FilerConfig.Load(box.ConfigPath);
        var notes = new List<(string Title, IReadOnlyList<string> Lines)>();

        Problems.Report(config, [@"C:\Staging\Notes\Stray 2026-11-13.docx"], [@"C:\Staging\Notes\Broken.docx"], (t, l) => notes.Add((t, l)));

        Assert.Equal(("Staging file matches no rule", "Stray 2026-11-13.docx"), (notes[0].Title, notes[0].Lines[0]));
        Assert.EndsWith("add a rule in doc-filer.json.", notes[0].Lines[^1]);
        Assert.Equal(("doc-filer couldn't convert", "Broken.docx"), (notes[1].Title, notes[1].Lines[0]));
        var item = Assert.Single(JsonNode.Parse(File.ReadAllText(queue.Replace("\\\\", "\\")))!["items"]!.AsArray())!;
        Assert.Equal(("transcripts", "unmatched", "Stray 2026-11-13.docx"), ((string?)item["source"], (string?)item["type"], (string?)item["files"]![0]));
    }

    [Fact]
    public void Without_notify_or_review_nothing_is_raised()
    {
        using var box = new Sandbox();
        var notes = 0;
        Problems.Report(FilerConfig.Load(box.ConfigPath), [@"C:\x\Stray.docx"], [@"C:\x\Broken.docx"], (_, _) => notes++);
        Assert.Equal(0, notes);
    }
}
