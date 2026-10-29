using System.Diagnostics;

namespace DocFiler;

public sealed class ProcessResult
{
    public int Converted { get; set; }
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public int Planned { get; set; }

    /// <summary>Files written into the repo, relative to its root, with forward slashes.</summary>
    public List<string> Written { get; } = [];

    /// <summary>Staging files that failed to convert.</summary>
    public List<string> FailedFiles { get; } = [];

    public int Failed => FailedFiles.Count;

    public override string ToString() =>
        Planned > 0
            ? $"{Planned} would be processed, {Skipped} unchanged"
            : $"{Converted} converted, {Copied} copied, {Skipped} unchanged, {Failed} failed";
}

public sealed class DirectoryProcessor(FilerConfig config, bool dryRun)
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

    private readonly ProcessResult _result = new();

    public ProcessResult Run()
    {
        // Two stages, and the order matters: everything is converted into the staging
        // markdown tree first, then copied from there into the repo. A botched conversion
        // never leaves a half-written file in the repo.
        foreach (var rule in config.AllConvertRules)
        {
            RunConvertRule(rule);
        }

        foreach (var rule in config.AllCopyRules)
        {
            RunCopyRule(rule);
        }

        return _result;
    }

    /// <summary>
    /// Files sitting in a convert rule's folder that no rule would touch. A misnamed file
    /// otherwise produces no error and no output, which is the worst failure an ingest tool
    /// has: "Catch Up" for "catch-up", or "-part-1" for "-part1", were each skipped silently.
    /// </summary>
    public static List<string> FindUnmatched(FilerConfig config)
    {
        var unmatched = new List<string>();
        var folders = config.AllConvertRules.Select(rule => Normalise(rule.From)).Distinct();

        foreach (var from in folders)
        {
            var directory = Path.GetFullPath(Path.Combine(config.SourceRoot, from));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var convertRules = config.AllConvertRules.Where(rule => Normalise(rule.From) == from).ToList();
            var copyRules = config.AllCopyRules.Where(rule => Normalise(rule.From) == from).ToList();

            foreach (var file in Directory.GetFiles(directory))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var converted = convertRules.Any(rule =>
                    MatchesExtension(file, rule.Extensions) && FileNameMapper.Map(rule, name) is not null);
                var copied = copyRules.Any(rule => MatchesExtension(file, rule.Extensions));

                if (!converted && !copied && !IsSystemFile(file))
                {
                    unmatched.Add(file);
                }
            }
        }

        return unmatched;
    }

    private static bool IsSystemFile(string file)
    {
        var name = Path.GetFileName(file);
        return name.StartsWith("~$") || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string path) => path.Replace('\\', '/').Trim('/');

    private void RunConvertRule(ConvertRule rule)
    {
        var sourceDirectory = Path.GetFullPath(Path.Combine(config.SourceRoot, rule.From));
        if (!Directory.Exists(sourceDirectory))
        {
            Log.Warn($"Skipping rule for '{rule.From}' — directory not found: {sourceDirectory}");
            return;
        }

        foreach (var sourceFile in Directory.GetFiles(sourceDirectory))
        {
            if (!MatchesExtension(sourceFile, rule.Extensions))
            {
                continue;
            }

            var mapping = FileNameMapper.Map(rule, Path.GetFileNameWithoutExtension(sourceFile));
            if (mapping is null)
            {
                continue;
            }

            var destinationDirectory = Path.GetFullPath(Path.Combine(sourceDirectory, mapping.Folder));
            var destinationFile = Path.Combine(destinationDirectory, mapping.FileName);

            if (IsUpToDate(sourceFile, destinationFile))
            {
                Log.Detail($"Skipping {sourceFile} as it has not been modified since last processing.");
                _result.Skipped++;
                continue;
            }

            if (dryRun)
            {
                Log.Plan($"{rule.Converter} {sourceFile} -> {destinationFile}");
                _result.Planned++;
                continue;
            }

            Directory.CreateDirectory(destinationDirectory);

            var converted = rule.Converter switch
            {
                Converter.Pandoc => RunPandoc(sourceFile, destinationFile, rule),
                Converter.Copy => CopyFile(sourceFile, destinationFile),
                _ => throw new InvalidOperationException($"Unknown converter: {rule.Converter}")
            };

            if (!converted)
            {
                _result.FailedFiles.Add(sourceFile);
                continue;
            }

            if (rule.Prettify && destinationFile.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(destinationFile, TranscriptPrettifier.Prettify(File.ReadAllText(destinationFile)));
            }

            _result.Converted++;
        }
    }

    private void RunCopyRule(CopyRule rule)
    {
        var sourceDirectory = Path.GetFullPath(Path.Combine(config.SourceRoot, rule.From));
        if (!Directory.Exists(sourceDirectory))
        {
            // Not a problem: a copy source only exists once something of that kind has been
            // converted, and warning every run would train the eye to ignore warnings that do matter.
            Log.Detail($"Nothing to copy from '{rule.From}' — not created yet.");
            return;
        }

        var searchOption = rule.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var destinationRoot = Path.GetFullPath(Path.Combine(config.ResolvedRepoRoot, rule.To));

        foreach (var sourceFile in Directory.GetFiles(sourceDirectory, "*", searchOption))
        {
            if (!MatchesExtension(sourceFile, rule.Extensions))
            {
                continue;
            }

            // Relative path rather than filename, so recursive rules keep their subfolders
            // (that is what puts per-person and per-series files into their own directories).
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destinationFile = Path.Combine(destinationRoot, relativePath);

            // Same bytes counts as unchanged. A source re-saved with identical content (a flow
            // that overwrites its output every hour, say) would otherwise be copied, and
            // reported as written, on every run.
            if (IsUpToDate(sourceFile, destinationFile) || SameContent(sourceFile, destinationFile))
            {
                Log.Detail($"Skipping {sourceFile} as it has not been modified since last processing.");
                _result.Skipped++;
                continue;
            }

            if (dryRun)
            {
                Log.Plan($"copy {sourceFile} -> {destinationFile}");
                _result.Planned++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            Log.Info($"Copying {sourceFile} to {destinationFile}");
            File.Copy(sourceFile, destinationFile, true);
            _result.Copied++;
            _result.Written.Add(Path.GetRelativePath(config.ResolvedRepoRoot, destinationFile).Replace('\\', '/'));
        }
    }

    private static bool MatchesExtension(string file, List<string> extensions) =>
        extensions.Count == 0 ||
        extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsUpToDate(string sourceFile, string destinationFile) =>
        File.Exists(destinationFile) &&
        File.GetLastWriteTimeUtc(sourceFile) <= File.GetLastWriteTimeUtc(destinationFile);

    private static bool SameContent(string sourceFile, string destinationFile)
    {
        if (!File.Exists(destinationFile) || new FileInfo(sourceFile).Length != new FileInfo(destinationFile).Length)
        {
            return false;
        }

        return File.ReadAllBytes(sourceFile).AsSpan().SequenceEqual(File.ReadAllBytes(destinationFile));
    }

    private static bool CopyFile(string sourceFile, string destinationFile)
    {
        Log.Info($"Copying {sourceFile} to {destinationFile}");
        File.Copy(sourceFile, destinationFile, true);
        return true;
    }

    /// <summary>
    /// Picks the pandoc input format from the source extension.
    ///
    /// This used to be hard-coded to "docx", which was fine while every source was a Teams
    /// transcript. Forms are not: people return a Word form in whatever they wrote it in, and
    /// one came back as a Chrome-saved HTML page. Passing that to the docx reader fails the
    /// file rather than converting it.
    ///
    /// The html reader is asked for "-native_divs-native_spans" deliberately. Without it,
    /// pandoc keeps the page's layout &lt;div&gt; and &lt;span&gt; wrappers as raw HTML in the
    /// markdown, which buries the prose in tag soup and makes the result unsearchable.
    /// </summary>
    private static string PandocInputFormat(string sourceFile) =>
        Path.GetExtension(sourceFile).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "html-native_divs-native_spans",
            _ => "docx"
        };

    private bool RunPandoc(string sourceFile, string destinationFile, ConvertRule rule)
    {
        var inputFormat = string.IsNullOrWhiteSpace(rule.InputFormat) ? PandocInputFormat(sourceFile) : rule.InputFormat;

        // Arguments go through ArgumentList rather than a command string, so paths containing
        // spaces need no quoting and there is no cmd.exe in the middle to re-parse them.
        var startInfo = new ProcessStartInfo(config.ResolvedPandoc)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-f", inputFormat, "-t", rule.Format, "-o", destinationFile }
        };

        if (!string.IsNullOrWhiteSpace(rule.Wrap))
        {
            startInfo.ArgumentList.Add($"--wrap={rule.Wrap}");
        }

        if (!string.IsNullOrWhiteSpace(rule.LuaFilter))
        {
            var filterPath = config.ResolveFilter(rule.LuaFilter);
            if (!File.Exists(filterPath))
            {
                Log.Error($"Lua filter not found at {filterPath} — refusing to convert {sourceFile} without it");
                return false;
            }

            startInfo.ArgumentList.Add($"--lua-filter={filterPath}");
        }

        // Source goes last so it cannot be mistaken for the argument of a preceding option.
        startInfo.ArgumentList.Add(sourceFile);

        Log.Info($"Converting {sourceFile} -> {destinationFile}");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Log.Error($"Failed to start pandoc for {sourceFile}");
            return false;
        }

        // Read both streams before waiting. Reading after the wait risks deadlocking if
        // pandoc ever fills the pipe buffer.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
        {
            Log.Error($"pandoc did not exit within {ProcessTimeout.TotalMinutes} minutes, killing it");
            process.Kill(true);
            return false;
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var errors = standardError.GetAwaiter().GetResult();

        if (!string.IsNullOrWhiteSpace(output))
        {
            Log.Info(output.Trim());
        }

        if (process.ExitCode != 0)
        {
            Log.Error($"pandoc failed on {sourceFile}: {errors.Trim()}");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(errors))
        {
            // Pandoc writes warnings to stderr on files it converts perfectly well, so this
            // is reported without failing the file.
            Log.Warn($"pandoc warning on {sourceFile}: {errors.Trim()}");
        }

        return true;
    }
}
