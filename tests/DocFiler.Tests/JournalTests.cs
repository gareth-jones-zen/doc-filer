using System.Text.Json;
using ActivityJournal;

namespace DocFiler.Tests;

/// <summary>1.7.0: each file a run files or archives is a line in the shared activity journal (background-tools-lib's
/// ActivityJournal), never in a dry run, and writing it never fails the run. Every name here is made up.</summary>
[Collection("Serial")]
public sealed class JournalTests : IDisposable
{
    private readonly Sandbox box = new();
    private readonly string journal;

    public JournalTests() => journal = Path.Combine(box.Root, "state", "journal.jsonl");

    public void Dispose() => box.Dispose();

    /// <summary>The sandbox's rules, with a journal, a copy rule's sensitivity and an archive rule as given.</summary>
    private FilerConfig Config(string? journalFile = "state/journal.jsonl", string copySensitivity = "", string archive = "")
    {
        var staging = JsonSerializer.Serialize(box.Staging, SupportJson.Default.String);
        var journalSetting = journalFile is null ? "" : $"\"journalFile\": {JsonSerializer.Serialize(journalFile, SupportJson.Default.String)},";
        var sensitivity = copySensitivity == "" ? "" : $", \"sensitivity\": \"{copySensitivity}\"";
        File.WriteAllText(box.ConfigPath, $$"""
            {
              "sourceRoot": {{staging}},
              "repoRoot": "repo",
              {{journalSetting}}
              {{(archive == "" ? "" : $"\"archive\": [{archive}],")}}
              "convert": [
                { "from": "Notes", "match": "^Note (?<date>\\d{4}-\\d{2}-\\d{2})$", "extensions": [".txt"],
                  "to": "markdown", "name": "{date}", "converter": "Copy" }
              ],
              "copy": [ { "from": "Notes/markdown", "extensions": [".md", ".txt"], "to": "out"{{sensitivity}} } ]
            }
            """);
        return FilerConfig.Load(box.ConfigPath);
    }

    private IReadOnlyList<JournalEntry> Lines() => File.Exists(journal) ? new Journal(journal).Read() : [];

    private static int Run(FilerConfig config, bool dryRun = false, string by = By.Hand) =>
        Filer.Run(config, dryRun, hook: false, [], TimeSpan.FromMinutes(1), by: by);

    [Fact]
    public void A_run_journals_each_file_it_filed_and_a_dry_run_nothing()
    {
        using var log = new LogCapture(box.Root);
        var config = Config();
        box.AddStaging("Note 2026-11-20.txt", "first draft");

        Assert.Equal(0, Run(config, dryRun: true));
        Assert.False(File.Exists(journal));

        Assert.Equal(0, Run(config));
        var line = Assert.Single(Lines());
        Assert.Equal("doc-filer", line.Tool);
        Assert.Equal("file", line.What);
        Assert.Equal(By.Hand, line.By);
        Assert.StartsWith("out/2026-11-20", line.Subject);
        Assert.Equal("new", line.Why);
        Assert.Equal(Sensitivity.Private, line.Sensitivity);
        Assert.Null(line.Undo);
        Assert.Null(line.Item);

        // Unchanged: nothing filed, nothing journalled.
        Assert.Equal(0, Run(config));
        Assert.Single(Lines());

        // Changed in staging: filed again, over the older copy.
        var staged = Path.Combine(box.Staging, "Notes", "Note 2026-11-20.txt");
        File.WriteAllText(staged, "second draft");
        File.SetLastWriteTimeUtc(staged, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(0, Run(config));
        Assert.Equal(2, Lines().Count);
        Assert.Equal("replaced the older copy: the staging file changed", Lines()[1].Why);
    }

    [Fact]
    public void The_watchers_runs_are_auto_and_a_rule_can_restrict_its_lines()
    {
        var config = Config(copySensitivity: "restricted");
        box.AddStaging("Note 2026-11-21.txt");

        Run(config, by: By.Auto);

        var line = Assert.Single(Lines());
        Assert.Equal(By.Auto, line.By);
        Assert.Equal(Sensitivity.Restricted, line.Sensitivity);
    }

    [Fact]
    public void An_archive_move_is_journalled_with_its_folder_and_why()
    {
        var notes = Path.Combine(box.Repo, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "2020-01-02-prep-sam.md"), "old prep");
        File.WriteAllText(Path.Combine(notes, $"{DateTime.Today:yyyy-MM-dd}-prep-sam.md"), "today's prep");
        var config = Config(archive: """{ "folder": "notes", "keepDays": 14, "to": "archive/{yyyy}-{mm}", "sensitivity": "normal" }""");

        Run(config, dryRun: true);
        Assert.False(File.Exists(journal));

        Run(config, by: By.Auto);
        var line = Assert.Single(Lines());
        Assert.Equal("archive", line.What);
        Assert.Equal(By.Auto, line.By);
        Assert.Equal("notes/2020-01-02-prep-sam.md -> notes/archive/2020-01/2020-01-02-prep-sam.md", line.Subject);
        Assert.Equal("notes", line.Rule);
        Assert.Equal("dated more than 14 days ago", line.Why);
        Assert.Equal(Sensitivity.Normal, line.Sensitivity);
    }

    [Fact]
    public void Without_journal_file_nothing_is_written()
    {
        var config = Config(journalFile: null);
        box.AddStaging("Note 2026-11-22.txt");

        Assert.Equal(0, Run(config));

        Assert.Null(config.ResolvedJournal);
        Assert.Empty(Directory.GetFiles(box.Root, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_journal_that_cant_be_written_is_logged_and_the_run_still_succeeds()
    {
        using var log = new LogCapture(box.Root);
        Directory.CreateDirectory(journal);   // a folder where the file should be
        var config = Config();
        box.AddStaging("Note 2026-11-23.txt");

        Assert.Equal(0, Run(config));

        Assert.Single(Directory.GetFiles(Path.Combine(box.Repo, "out")));
        Assert.Contains(log.Lines, l => l.Contains("WARNING: journal:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_sensitivity_is_a_config_problem()
    {
        var error = Assert.Throws<InvalidDataException>(() => Config(copySensitivity: "secret"));
        Assert.Contains("copy rule for 'Notes/markdown': sensitivity is one of normal, private, restricted, not 'secret'.", error.Message);
    }

    [Fact]
    public void Journal_file_comes_from_a_base_with_its_path_variables()
    {
        var tools = Path.Combine(box.Root, "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "base-config.toml"), $"""
            journal_file = '%SHARED%/journal.jsonl'

            [paths]
            SHARED = '{box.Root}/shared'
            """);
        var path = Path.Combine(tools, "doc-filer.toml");
        File.WriteAllText(path, $"base = 'base-config.toml'\nsource_root = '{box.Staging}'\nrepo_root = '../repo'\n");

        Assert.Equal(Path.GetFullPath(Path.Combine(box.Root, "shared", "journal.jsonl")), FilerConfig.Load(path).ResolvedJournal);
    }
}
