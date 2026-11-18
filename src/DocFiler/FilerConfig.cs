using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RepoCommit;

namespace DocFiler;

/// <summary>
/// The whole of the tool's behaviour lives in the config file. Nothing here should need
/// editing to add a person, a meeting series, a report folder or a self-test case.
/// </summary>
public sealed class FilerConfig
{
    /// <summary>Absolute path to the staging folder files are saved into (outside the target repo).</summary>
    public string SourceRoot { get; set; } = "";

    /// <summary>Path to the target repo's root, relative to the config file.</summary>
    public string RepoRoot { get; set; } = "";

    /// <summary>
    /// Path to the pandoc executable, relative to the config file. Empty means the copy
    /// that ships with the tool (pandoc\ beside the exe), or "pandoc" on the PATH.
    /// </summary>
    public string Pandoc { get; set; } = "";

    /// <summary>One entry per person whose files are routed by initials. Expanded through the person templates.</summary>
    public List<Person> People { get; set; } = [];

    /// <summary>Convert rules generated once per person. Supports {initials}.</summary>
    public List<ConvertRule> PersonConvertTemplates { get; set; } = [];

    /// <summary>Copy rules generated once per person. Supports {initials} and {destination}.</summary>
    public List<CopyRule> PersonCopyTemplates { get; set; } = [];

    /// <summary>Standalone convert rules.</summary>
    public List<ConvertRule> Convert { get; set; } = [];

    /// <summary>Standalone copy rules, staging tree into the target repo.</summary>
    public List<CopyRule> Copy { get; set; } = [];

    /// <summary>
    /// A command run after any run that wrote something into the repo, failed a file, or
    /// (in watch mode) found a new file no rule matches. The first element is the program,
    /// the rest its arguments. It runs from the repo root and gets a JSON report on stdin.
    /// Since 1.4.0 it can be a list of commands, each run in turn with the same report.
    /// See README.md, "The after-run hook".
    /// </summary>
    public JsonNode? AfterRun { get; set; }

    /// <summary>The after-run commands, each a program and its arguments (from <see cref="AfterRun"/>).</summary>
    [JsonIgnore] public List<List<string>> AfterRunCommands { get; private set; } = [];

    /// <summary>
    /// Optional (1.4.0): true, and the tool notifies its own problems: a staging file no rule matches (once, in watch
    /// mode) and a file it couldn't convert. Until 1.4.0 that was left to an after-run hook.
    /// </summary>
    public bool Notify { get; set; }

    /// <summary>
    /// Optional (1.4.0): a review queue (background-tools-lib's ReviewQueue: file, source, lock) where a staging file no
    /// rule matches is raised, once, as an "unmatched" item, for whoever reads the queue to rename it or add a rule.
    /// </summary>
    public JsonNode? Review { get; set; }

    [JsonIgnore] public ReviewQueue.ReviewSettings? ReviewSettings { get; private set; }

    /// <summary>
    /// Optional: commit what each run files into the repo (with --hook, and always in watch mode), only those files,
    /// after the afterRun hook. Keys: repo (relative to the config file), commit, push, trailers. See README.md,
    /// "Committing to git".
    /// </summary>
    public JsonNode? Git { get; set; }

    public WatchSettings Watch { get; set; } = new();

    /// <summary>
    /// Optional (1.2.0): the log file, for every run and for the watcher (autostart on uses it). Relative to the config
    /// file; ~ and %VARIABLES% are expanded. --log, where given, wins.
    /// </summary>
    public string Log { get; set; } = "";

    /// <summary>
    /// Optional (1.2.0): true runs the afterRun hook and the git commit after a one-off run too, as --hook does, so a
    /// run by hand leaves the repo as the watcher would. --no-hook skips them for one run.
    /// </summary>
    public bool Hook { get; set; }

    /// <summary>
    /// Folders in the repo whose dated files move into an archive once they're old enough, so the
    /// folder holds only what's current. Runs on every run, and once a day in watch mode.
    /// </summary>
    public List<ArchiveRule> Archive { get; set; } = [];

    /// <summary>Filename routing checks for --self-test.</summary>
    public List<SelfTestCase> SelfTest { get; set; } = [];

    // ---- resolved at load time, not present in the JSON ----

    [JsonIgnore] public string ConfigPath { get; private set; } = "";
    [JsonIgnore] public string ConfigDirectory { get; private set; } = "";
    [JsonIgnore] public string ResolvedRepoRoot { get; private set; } = "";
    [JsonIgnore] public string ResolvedPandoc { get; private set; } = "";
    [JsonIgnore] public List<ConvertRule> AllConvertRules { get; private set; } = [];
    [JsonIgnore] public List<CopyRule> AllCopyRules { get; private set; } = [];
    [JsonIgnore] public GitSettings? GitSettings { get; private set; }
    [JsonIgnore] public string? ResolvedLog { get; private set; }

    /// <summary>The base file a TOML config named (1.3.0), or null.</summary>
    [JsonIgnore] public string? BasePath { get; private set; }

    /// <summary>With a base file: its [paths] as variables, then the environment, for the path settings that expand
    /// %VARIABLES%. Null without one, which expands from the environment as before.</summary>
    [JsonIgnore] internal Func<string, string?>? Variables { get; private set; }

    /// <summary>
    /// Every top-level key a TOML config takes, as written in TOML (snake_case). A base file's settings are used only
    /// when they're one of these: see README.md, "Sharing settings with other tools".
    /// </summary>
    public static readonly string[] KnownKeys =
    [
        "source_root", "repo_root", "pandoc", "people", "person_convert_templates", "person_copy_templates", "convert",
        "copy", "after_run", "git", "watch", "log", "hook", "archive", "self_test", "notify", "review",
    ];

    public static FilerConfig Load(string configPath)
    {
        var text = File.ReadAllText(configPath);
        string json;
        Func<string, string?>? variables = null;
        string? basePath = null;
        if (TomlConfig.IsToml(configPath))
        {
            (json, variables, basePath) = TomlConfig.Read(text, configPath, Environment.GetEnvironmentVariable);
        }
        else
        {
            json = text;
        }

        var config = Json.ReadConfig(json)
                     ?? throw new InvalidDataException($"Could not parse {configPath}");

        config.BasePath = basePath;
        config.Variables = variables;
        config.ConfigPath = Path.GetFullPath(configPath);
        config.ConfigDirectory = Path.GetDirectoryName(config.ConfigPath)!;
        config.ResolvedRepoRoot = Path.GetFullPath(Path.Combine(config.ConfigDirectory, config.RepoRoot));
        config.ResolvedLog = string.IsNullOrWhiteSpace(config.Log) ? null : ExpandPath(config.Log, config.ConfigDirectory, variables);
        // A config's own pandoc wins; then one named in DOC_FILER_PANDOC; then the copy that
        // ships with the tool, unpacked beside the exe; then pandoc on the PATH.
        var named = Environment.GetEnvironmentVariable("DOC_FILER_PANDOC");
        config.ResolvedPandoc = !string.IsNullOrWhiteSpace(config.Pandoc)
            ? Path.GetFullPath(Path.Combine(config.ConfigDirectory, config.Pandoc))
            : !string.IsNullOrWhiteSpace(named) ? named : Bundled.Pandoc() ?? "pandoc";

        config.ExpandPersonTemplates();
        config.Validate();
        return config;
    }

    /// <summary>A path setting: ~ is the home folder, %VARIABLES% are expanded, and a relative path is relative to
    /// the config file's folder. With <paramref name="variables"/> (a base file's [paths], then the environment), a
    /// %NAME% is looked up there; one it doesn't know is left as written, as Windows does.</summary>
    internal static string ExpandPath(string value, string configDirectory, Func<string, string?>? variables = null)
    {
        var expanded = variables is null
            ? Environment.ExpandEnvironmentVariables(value.Trim())
            : System.Text.RegularExpressions.Regex.Replace(value.Trim(), "%([^%]+)%", match => variables(match.Groups[1].Value) ?? match.Value);
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded.Length > 2 ? expanded[2..] : "");
        }

        return Path.GetFullPath(Path.Combine(configDirectory, expanded));
    }

    /// <summary>
    /// A rule's Lua filter: a path relative to the config file if one exists there, otherwise
    /// one of the filters shipped with the tool (filters/, copied next to the executable).
    /// </summary>
    public string ResolveFilter(string luaFilter)
    {
        var besideConfig = Path.GetFullPath(Path.Combine(ConfigDirectory, luaFilter));
        if (File.Exists(besideConfig))
        {
            return besideConfig;
        }

        return Path.Combine(AppContext.BaseDirectory, "filters", luaFilter);
    }

    private void ExpandPersonTemplates()
    {
        AllConvertRules = [.. Convert];
        AllCopyRules = [.. Copy];

        // Rules are cloned whole and then substituted. Copying fields one by one, as this
        // used to, silently dropped every new rule setting until someone remembered to add
        // it here too — it happened three times.
        foreach (var person in People)
        {
            foreach (var template in PersonConvertTemplates)
            {
                var rule = template.Clone();
                rule.From = Substitute(rule.From, person);
                rule.Match = Substitute(rule.Match, person);
                rule.To = Substitute(rule.To, person);
                rule.Name = Substitute(rule.Name, person);
                AllConvertRules.Add(rule);
            }

            foreach (var template in PersonCopyTemplates)
            {
                var rule = template.Clone();
                rule.From = Substitute(rule.From, person);
                rule.To = Substitute(rule.To, person);
                AllCopyRules.Add(rule);
            }
        }
    }

    private static string Substitute(string template, Person person) =>
        template.Replace("{initials}", person.Initials)
                .Replace("{destination}", person.Destination);

    private void Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(SourceRoot))
        {
            problems.Add("sourceRoot is not set.");
        }
        else if (!Directory.Exists(SourceRoot))
        {
            problems.Add($"sourceRoot does not exist: {SourceRoot}");
        }

        if (!Directory.Exists(ResolvedRepoRoot))
        {
            problems.Add($"repoRoot does not resolve to a directory: {ResolvedRepoRoot}");
        }

        if (Path.IsPathRooted(ResolvedPandoc) && !File.Exists(ResolvedPandoc) &&
            AllConvertRules.Any(rule => rule.Converter == Converter.Pandoc))
        {
            problems.Add($"pandoc not found at {ResolvedPandoc}");
        }

        foreach (var rule in AllConvertRules.Where(rule => string.IsNullOrWhiteSpace(rule.Match)))
        {
            problems.Add($"convert rule for '{rule.From}' has no match pattern.");
        }

        AfterRunCommands = [];
        if (AfterRun is JsonArray commands)
        {
            if (commands.All(c => c is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String))
            {
                if (commands.Count > 0)
                    AfterRunCommands.Add([.. commands.Select(c => c!.GetValue<string>())]);
            }
            else if (commands.All(c => c is JsonArray a && a.Count > 0 && a.All(x => x is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String)))
            {
                AfterRunCommands.AddRange(commands.Select(c => c!.AsArray().Select(x => x!.GetValue<string>()).ToList()));
            }
            else
            {
                problems.Add("afterRun should be a command (a list of text) or a list of commands.");
            }
        }
        else if (AfterRun is not null)
        {
            problems.Add("afterRun should be a command (a list of text) or a list of commands.");
        }

        if (Review is not null)
        {
            try
            {
                var table = Review is JsonObject o
                    ? o.ToDictionary(kv => kv.Key, kv => (object)(kv.Value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : kv.Value?.ToJsonString() ?? ""))
                    : null;
                ReviewSettings = ReviewQueue.ReviewSettings.Read(table, ConfigPath, path => ExpandPath(path, ConfigDirectory, Variables));
            }
            catch (FormatException exception)
            {
                problems.Add(exception.Message);
            }
        }

        if (Git is not null)
        {
            try
            {
                GitSettings = GitSettings.Read(Git, ConfigPath, path => Path.GetFullPath(Path.Combine(ConfigDirectory, path)), "\"git\"");
                if (!Directory.Exists(GitSettings.Repo))
                {
                    problems.Add($"git: repo does not resolve to a directory: {GitSettings.Repo}");
                }
            }
            catch (FormatException exception)
            {
                problems.Add(exception.Message);
            }
        }

        foreach (var rule in Archive)
        {
            if (string.IsNullOrWhiteSpace(rule.Folder))
            {
                problems.Add("an archive rule has no folder.");
            }

            var unknown = System.Text.RegularExpressions.Regex.Matches(rule.CommitMessage, @"\{[^}]*\}")
                .Select(match => match.Value).Where(field => !RepoCommits.ArchivePlaceholders.Contains(field)).ToList();
            if (unknown.Count > 0)
            {
                problems.Add($"archive rule for '{rule.Folder}': commitMessage can use {string.Join(", ", RepoCommits.ArchivePlaceholders)}, not {string.Join(", ", unknown)}.");
            }
            try
            {
                if (!new System.Text.RegularExpressions.Regex(rule.Match).GetGroupNames().Contains("date"))
                {
                    problems.Add($"archive rule for '{rule.Folder}': match has no (?<date>) group.");
                }
            }
            catch (ArgumentException exception)
            {
                problems.Add($"archive rule for '{rule.Folder}': match isn't a valid regex ({exception.Message}).");
            }
        }

        foreach (var filter in AllConvertRules.Select(rule => rule.LuaFilter)
                     .Where(filter => !string.IsNullOrWhiteSpace(filter)).Distinct())
        {
            if (!File.Exists(ResolveFilter(filter)))
            {
                problems.Add($"Lua filter '{filter}' not found beside the config or in the tool's filters/ folder.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException(
                $"Configuration problems in {ConfigPath}:{Environment.NewLine}  - " +
                string.Join($"{Environment.NewLine}  - ", problems));
        }
    }
}

public sealed class Person
{
    /// <summary>Filename prefix used in the staging folder, e.g. "AB".</summary>
    public string Initials { get; set; } = "";

    /// <summary>Where their files land in the target repo, e.g. "team/alex-brown".</summary>
    public string Destination { get; set; } = "";
}

public enum Converter
{
    /// <summary>Run pandoc to turn a .docx or .html into markdown.</summary>
    Pandoc,

    /// <summary>Straight file copy (for .txt transcripts that need no conversion).</summary>
    Copy
}

/// <summary>
/// Converts files inside the staging folder. Source and destination are both in the
/// staging area — getting things into the repo is the job of <see cref="CopyRule"/>.
/// </summary>
public sealed class ConvertRule
{
    /// <summary>Directory to scan, relative to sourceRoot. Not recursive.</summary>
    public string From { get; set; } = "";

    /// <summary>
    /// Regex matched against the filename without its extension. Named groups:
    /// (?&lt;date&gt;) required, (?&lt;label&gt;) optional, (?&lt;part&gt;) optional.
    /// </summary>
    public string Match { get; set; } = "";

    /// <summary>File extensions this rule applies to, e.g. [".docx"].</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>Destination directory, relative to <see cref="From"/>. Supports {label-slug}.</summary>
    public string To { get; set; } = "";

    /// <summary>Output filename template. Supports {date}, {label}, {label-slug}, {part}.</summary>
    public string Name { get; set; } = "{date}{part}";

    public Converter Converter { get; set; } = Converter.Pandoc;

    /// <summary>
    /// Pandoc output format. Defaults to "markdown", pandoc's own flavour, because that is
    /// what existing transcripts were produced with, and changing it would rewrite all of them.
    ///
    /// Set it to "gfm" for anything table-heavy. Pandoc's markdown renders a table it cannot
    /// express as pipes using *grid tables* (`+----+----+`), which is valid pandoc markdown and
    /// renders as a wall of plus signs everywhere else — VS Code's preview included. GFM falls
    /// back to raw HTML tables instead, which preview correctly.
    /// </summary>
    public string Format { get; set; } = "markdown";

    /// <summary>
    /// Optional pandoc Lua filter: a path relative to the config file, or the name of one of
    /// the tool's own filters (see <see cref="FilerConfig.ResolveFilter"/>).
    ///
    /// flatten-tables.lua turns Word's layout tables into pipe tables. Without it pandoc falls
    /// back to raw HTML for any table with merged or multi-block cells, and no choice of
    /// output format avoids that — it is a property of the table, not the writer.
    /// </summary>
    public string LuaFilter { get; set; } = "";

    /// <summary>
    /// Optional pandoc input format, overriding the one picked from the file extension.
    ///
    /// The default for HTML switches native spans off, which strips the layout wrappers
    /// from a browser-saved page. OneNote pages need the opposite: OneNote writes bold as
    /// &lt;span style="font-weight:bold"&gt;, so with spans switched off the bold is lost
    /// before any Lua filter can see it. A OneNote rule sets "html" and lets
    /// onenote-clean.lua decide which spans carry meaning.
    /// </summary>
    public string InputFormat { get; set; } = "";

    /// <summary>
    /// Optional pandoc --wrap value: "auto", "none" or "preserve". Empty leaves pandoc's
    /// default.
    /// </summary>
    public string Wrap { get; set; } = "";

    /// <summary>Apply the Teams transcript clean-up to the resulting markdown.</summary>
    public bool Prettify { get; set; } = true;

    public ConvertRule Clone()
    {
        var clone = (ConvertRule)MemberwiseClone();
        clone.Extensions = [.. Extensions];
        return clone;
    }
}

/// <summary>Copies finished markdown (and report originals) out of the staging folder into the repo.</summary>
public sealed class CopyRule
{
    /// <summary>Directory to copy from, relative to sourceRoot.</summary>
    public string From { get; set; } = "";

    public List<string> Extensions { get; set; } = [];

    /// <summary>Destination directory, relative to the repo root.</summary>
    public string To { get; set; } = "";

    /// <summary>Include subdirectories, preserving their structure at the destination.</summary>
    public bool Recurse { get; set; }

    public CopyRule Clone()
    {
        var clone = (CopyRule)MemberwiseClone();
        clone.Extensions = [.. Extensions];
        return clone;
    }
}

/// <summary>
/// Moves files dated more than <see cref="KeepDays"/> ago out of a repo folder into an archive under
/// it. Only the folder's top level is looked at, so files already archived stay put. A file with no
/// date in its name is left alone, and nothing is ever overwritten or deleted.
/// </summary>
public sealed class ArchiveRule
{
    /// <summary>The folder to keep tidy, relative to the repo root.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Files dated within this many days stay where they are.</summary>
    public int KeepDays { get; set; } = 14;

    public List<string> Extensions { get; set; } = [".md"];

    /// <summary>Regex over the filename without its extension; (?&lt;date&gt;) must be yyyy-mm-dd.</summary>
    public string Match { get; set; } = @"^(?<date>\d{4}-\d{2}-\d{2})";

    /// <summary>Where a file goes, relative to <see cref="Folder"/>. Supports {yyyy}, {mm} and {dd}.</summary>
    public string To { get; set; } = "archive/{yyyy}-{mm}";

    /// <summary>
    /// With a git section: the archive commit's subject, with {count} (files moved), {folder} and {keepDays}. Empty
    /// means "Archive {count} file(s) from {folder} older than {keepDays} days".
    /// </summary>
    public string CommitMessage { get; set; } = "";
}

public sealed class WatchSettings
{
    /// <summary>How often watch mode lists the staging folders.</summary>
    public int CheckEverySeconds { get; set; } = 30;

    /// <summary>
    /// How long the staging folders must stay unchanged before a run. A file OneDrive is
    /// still syncing is otherwise caught half-written.
    /// </summary>
    public int SettleSeconds { get; set; } = 60;
}

/// <summary>
/// One --self-test case: a file in a staging folder, and where it should be converted to.
/// Rules are scoped to a directory, so two rules matching the same name in different
/// folders is not a conflict.
/// </summary>
public sealed class SelfTestCase
{
    /// <summary>Staging directory the file sits in, relative to sourceRoot.</summary>
    public string From { get; set; } = "";

    /// <summary>The filename, with its extension.</summary>
    public string File { get; set; } = "";

    /// <summary>Expected output folder, relative to <see cref="From"/>.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Expected output filename.</summary>
    public string Name { get; set; } = "";

    /// <summary>Why the case exists: printed when it fails.</summary>
    public string Why { get; set; } = "";
}
