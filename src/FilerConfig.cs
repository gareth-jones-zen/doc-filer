using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// bundled with the tool (vendor/, unpacked by run.ps1), or "pandoc" on the PATH.
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
    /// See README.md, "The after-run hook".
    /// </summary>
    public List<string> AfterRun { get; set; } = [];

    public WatchSettings Watch { get; set; } = new();

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

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static FilerConfig Load(string configPath)
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<FilerConfig>(json, SerializerOptions)
                     ?? throw new InvalidDataException($"Could not parse {configPath}");

        config.ConfigPath = Path.GetFullPath(configPath);
        config.ConfigDirectory = Path.GetDirectoryName(config.ConfigPath)!;
        config.ResolvedRepoRoot = Path.GetFullPath(Path.Combine(config.ConfigDirectory, config.RepoRoot));
        // A config's own pandoc wins; otherwise the copy bundled with the tool, which run.ps1
        // unpacks and names in DOC_FILER_PANDOC; otherwise pandoc on the PATH.
        var bundled = Environment.GetEnvironmentVariable("DOC_FILER_PANDOC");
        config.ResolvedPandoc = !string.IsNullOrWhiteSpace(config.Pandoc)
            ? Path.GetFullPath(Path.Combine(config.ConfigDirectory, config.Pandoc))
            : !string.IsNullOrWhiteSpace(bundled) ? bundled : "pandoc";

        config.ExpandPersonTemplates();
        config.Validate();
        return config;
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

        foreach (var rule in Archive)
        {
            if (string.IsNullOrWhiteSpace(rule.Folder))
            {
                problems.Add("an archive rule has no folder.");
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
