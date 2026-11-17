using System.Text.Json;
using System.Text.Json.Nodes;
using WinBackground;

namespace DocFiler.Tests;

/// <summary>The config's git section: what's committed after a run is exactly what the EA's after-run hook
/// (doc_filer_hook.py, which committed for this tool until git) did for the same report. Golden/hooks.json is that hook
/// run, unchanged, on each case in a temporary repository: its commits (message, name-status), git status after and its
/// notifications. Two things differ, by design: the ingest message says where it came from as "Filed automatically by
/// doc-filer (tools/doc-filer.json)." rather than naming the hook, and an archive commit that fails is notified with its
/// whole subject ("1 prep note(s) older than two weeks", not "1 prep note(s)").</summary>
[Collection("Serial")]
public sealed class GitTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("doc-filer-git-").FullName;
    private readonly List<(string Title, IReadOnlyList<string> Lines)> notes = [];

    public void Dispose() => GitRepo.Delete(root);

    private static JsonArray Golden() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "hooks.json")))!.AsArray();

    public static TheoryData<string> Cases() => [.. Golden().Select(g => (string)g!["name"]!)];

    private void Notify(string title, IReadOnlyList<string> lines) => notes.Add((title, lines));

    private JsonArray Notes() => new([.. notes.Select(n => (JsonNode?)new JsonArray(JsonValue.Create(n.Title),
        new JsonArray([.. n.Lines.Select(l => (JsonNode?)JsonValue.Create(l))])))]);

    /// <summary>The EA's layout: the config in tools/ of the repository, which is the repo root.</summary>
    private FilerConfig EaConfig(string repo, string git = """{ "repo": "..", "trailers": ["Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"] }""",
        string archiveExtra = """, "commitMessage": "Archive {count} prep note(s) older than two weeks" """)
    {
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        var path = Path.Combine(repo, "tools", "doc-filer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "sourceRoot": {{JsonSerializer.Serialize(staging, SupportJson.Default.String)}},
              "repoRoot": "..",
              "archive": [ { "folder": "meetings/preps", "keepDays": 14 {{archiveExtra}} } ],
              "git": {{git}}
            }
            """);
        return FilerConfig.Load(path);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Commits_are_the_python_hooks(string name)
    {
        var golden = Golden().Single(g => (string)g!["name"]! == name)!.AsObject();
        var repo = new GitRepo(Path.Combine(root, "repo"), golden["committed"] as JsonObject, golden["failing_hook"] is not null);
        foreach (var move in golden["move"]?.AsArray() ?? [])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(repo.File((string)move!["to"]!))!);
            File.Move(repo.File((string)move["from"]!), repo.File((string)move["to"]!));
        }
        repo.Write(golden["untracked"] as JsonObject);
        repo.Write(golden["written"] as JsonObject);
        var config = EaConfig(repo.Path);
        var result = new ProcessResult();
        foreach (var path in golden["report"]!["written"]!.AsArray())
            result.Written.Add((string)path!);
        foreach (var move in golden["report"]!["moved"]!.AsArray())
            result.Moved.Add(new MovedFile((string)move!["from"]!, (string)move["to"]!));

        var previous = Log.FilePath;
        Log.FilePath = Path.Combine(root, "doc-filer.log");
        try
        {
            RepoCommits.Run(config, result, Notify);
        }
        finally
        {
            Log.FilePath = previous;
        }

        var commits = golden["commits"]!.ToJsonString().Replace("Filed automatically by doc-filer (tools/doc_filer_hook.py).",
            "Filed automatically by doc-filer (tools/doc-filer.json).", StringComparison.Ordinal);
        Assert.Equal(commits, repo.Commits().ToJsonString());
        Assert.Equal(golden["status"]!.ToJsonString(), repo.Status().ToJsonString());
        Assert.Equal(golden["notes"]!.ToJsonString().Replace("[\"1 prep note(s)\",", "[\"1 prep note(s) older than two weeks\",",
            StringComparison.Ordinal), Notes().ToJsonString());
    }

    [Fact]
    public void A_run_commits_after_the_hook_and_only_with_hook_and_not_a_dry_run()
    {
        using var box = new Sandbox();
        var repo = new GitRepo(box.Repo);
        try
        {
            var seen = Path.Combine(box.Root, "seen-by-hook.txt");
            box.WriteConfig(60, afterRun: [Shell.PowerShellExe, "-NoProfile", "-NonInteractive", "-Command",
                $"git status --porcelain -uall | Out-File -Encoding ascii '{seen}'"]);
            var text = File.ReadAllText(box.ConfigPath).Replace("\"watch\":", "\"git\": { \"repo\": \"repo\", \"trailers\": [\"Co-Authored-By: X <x@example.com>\"] },\n  \"watch\":");
            File.WriteAllText(box.ConfigPath, text);
            var config = FilerConfig.Load(box.ConfigPath);
            using var log = new LogCapture(box.Root);

            box.AddStaging("Note 2026-11-13.txt", "a note");
            Filer.Run(config, dryRun: true, hook: true, [], TimeSpan.FromSeconds(10), notify: Notify);
            Filer.Run(config, dryRun: false, hook: false, [], TimeSpan.FromSeconds(10), notify: Notify);
            Assert.Empty(repo.Commits());

            box.AddStaging("Note 2026-11-14.txt", "another");
            Filer.Run(config, dryRun: false, hook: true, [], TimeSpan.FromSeconds(10), notify: Notify);
            var commit = Assert.Single(repo.Commits());
            Assert.Equal("Ingest out 2026-11-14\n\nFiled automatically by doc-filer (doc-filer.json).\n\nCo-Authored-By: X <x@example.com>",
                (string)commit!["message"]!);   // the config is outside the repository: its name alone
            Assert.Contains("?? out/2026-11-14.md", File.ReadAllText(seen));   // the hook saw it before it was committed
            Assert.Contains("?? out/2026-11-13.md", repo.Status().Select(s => (string)s!));   // filed without --hook: left alone
            Assert.Contains(log.Lines, l => l.Contains("git: committed 1 path(s) as ", StringComparison.Ordinal));
            Assert.Empty(notes);
        }
        finally
        {
            GitRepo.Delete(Path.Combine(box.Repo, ".git"));
        }
    }

    [Fact]
    public void An_archive_rule_without_a_commit_message_says_what_it_moved()
    {
        var repo = new GitRepo(Path.Combine(root, "repo"), new JsonObject { ["meetings/preps/2026-10-01-a.md"] = "a\nlines\n" });
        Directory.CreateDirectory(repo.File("meetings/preps/archive/2026-10"));
        File.Move(repo.File("meetings/preps/2026-10-01-a.md"), repo.File("meetings/preps/archive/2026-10/2026-10-01-a.md"));
        var config = EaConfig(repo.Path, """{ "repo": ".." }""", "");
        var result = new ProcessResult();
        result.Moved.Add(new MovedFile("meetings/preps/2026-10-01-a.md", "meetings/preps/archive/2026-10/2026-10-01-a.md"));
        RepoCommits.Run(config, result, Notify);
        Assert.Equal("Archive 1 file(s) from meetings/preps older than 14 days\n\nMoved by doc-filer's archive rule (tools/doc-filer.json).",
            (string)repo.Commits().Single()!["message"]!);
    }

    [Fact]
    public void No_git_section_commits_nothing()
    {
        var repo = new GitRepo(Path.Combine(root, "repo"));
        repo.Write(new JsonObject { ["a.md"] = "a\n" });
        var config = EaConfig(repo.Path).GitSettings!;
        Assert.True(config.Commit);
        var path = Path.Combine(repo.Path, "tools", "doc-filer.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"git\":", "\"unused\":"));
        var none = FilerConfig.Load(path);
        Assert.Null(none.GitSettings);
        var result = new ProcessResult();
        result.Written.Add("a.md");
        RepoCommits.Run(none, result, Notify);
        Assert.Empty(repo.Commits());
    }

    [Theory]
    [InlineData("""{ "repo": "..", "repository": "x" }""", "repository under \"git\" in ")]
    [InlineData("""{ "push": true }""", "needs repo")]
    [InlineData("""{ "repo": "no-such-folder" }""", "git: repo does not resolve to a directory")]
    [InlineData("""{ "repo": "..", "trailers": "Co-Authored-By: X" }""", "trailers under \"git\" in ")]
    [InlineData("""[]""", "git must be a \"git\" table")]
    public void Bad_git_settings_are_refused(string git, string message)
    {
        var repo = Path.Combine(root, "plain");
        Directory.CreateDirectory(repo);
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => EaConfig(repo, git)).Message);
    }

    [Fact]
    public void An_archive_commit_message_can_only_use_its_placeholders() =>
        Assert.Contains("commitMessage can use {count}, {folder}, {keepDays}, not {date}",
            Assert.Throws<InvalidDataException>(() => EaConfig(root, archiveExtra: """, "commitMessage": "Archive {count} from {date}" """)).Message);
}
