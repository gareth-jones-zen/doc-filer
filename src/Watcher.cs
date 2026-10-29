namespace DocFiler;

/// <summary>
/// Watch mode: stays running, and files whatever lands in the staging folders.
///
/// It lists the folders the rules read from on a timer and compares sizes and modified
/// times. File-change events are not used: on OneDrive-synced folders they are unreliable,
/// and a missed event means a file that never gets filed. A change starts a settle
/// period, and the run happens only once the folders have stopped changing, so a file
/// OneDrive is still writing is not caught half-done.
///
/// The tool's own output also lives in the staging tree (the markdown intermediates), so
/// the listing is retaken after every run: only changes made by something else count.
/// </summary>
public sealed class Watcher(string configPath)
{
    private static readonly TimeSpan LockWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromHours(1);

    private HashSet<string> _knownUnmatched = new(StringComparer.OrdinalIgnoreCase);
    private string _unmatchedState = "";

    public int Run()
    {
        var config = FilerConfig.Load(configPath);
        var configStamp = File.GetLastWriteTimeUtc(configPath);
        Log.Info($"Watching {config.SourceRoot} for {config.ResolvedRepoRoot} " +
                 $"(every {config.Watch.CheckEverySeconds}s, settle {config.Watch.SettleSeconds}s; config {config.ConfigPath})");

        // Each unmatched file is reported once, ever, not once per start (which would be every
        // logon), so what has been reported is kept between runs. Anything saved while the
        // watcher was down is caught by the first run, below.
        _unmatchedState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "doc-filer", $"unmatched-{RunLock.Key(config.ConfigPath)}.txt");
        if (File.Exists(_unmatchedState))
        {
            _knownUnmatched = new HashSet<string>(File.ReadAllLines(_unmatchedState), StringComparer.OrdinalIgnoreCase);
        }

        RunOnce(config);
        var snapshot = Snapshot(config);
        DateTime? changedAt = null;
        var beat = DateTime.Now;

        while (true)
        {
            Thread.Sleep(TimeSpan.FromSeconds(Math.Max(5, config.Watch.CheckEverySeconds)));
            try
            {
                // A line an hour, so a silent log means a stuck watcher rather than a quiet day.
                if (DateTime.Now - beat >= Heartbeat)
                {
                    beat = DateTime.Now;
                    Log.Info("alive");
                }

                // A config edit (a new person, a new rule) takes effect without a restart. A
                // broken edit keeps the old config running rather than stopping the watch.
                var stamp = File.GetLastWriteTimeUtc(configPath);
                if (stamp != configStamp)
                {
                    configStamp = stamp;
                    try
                    {
                        config = FilerConfig.Load(configPath);
                        snapshot = [];
                        Log.Info("Config changed; reloaded");
                    }
                    catch (Exception exception)
                    {
                        Log.Error($"Config changed but didn't load, so the old one stays in use: {exception.Message}");
                    }
                }

                var current = Snapshot(config);
                if (!SameSnapshot(current, snapshot))
                {
                    snapshot = current;
                    changedAt = DateTime.Now;
                    continue;
                }

                if (changedAt is null || DateTime.Now - changedAt < TimeSpan.FromSeconds(config.Watch.SettleSeconds))
                {
                    continue;
                }

                changedAt = null;
                RunOnce(config);
                snapshot = Snapshot(config);
            }
            catch (Exception exception)
            {
                Log.Error($"Watch pass failed, will try again: {exception.Message}");
            }
        }
    }

    private void RunOnce(FilerConfig config)
    {
        var unmatched = DirectoryProcessor.FindUnmatched(config);
        var before = _knownUnmatched.Count;
        var fresh = unmatched.Where(file => _knownUnmatched.Add(file)).ToList();
        // Forget files that have gone, so one saved again under the same name is reported again.
        _knownUnmatched.IntersectWith(unmatched);
        if (fresh.Count > 0 || _knownUnmatched.Count != before)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_unmatchedState)!);
            File.WriteAllLines(_unmatchedState, _knownUnmatched.Order());
        }

        foreach (var file in fresh)
        {
            Log.Warn($"No rule matches {file}");
        }

        Filer.Run(config, dryRun: false, hook: true, fresh, LockWait);
    }

    /// <summary>Size and modified time of every file in every folder a rule reads from.</summary>
    private static Dictionary<string, (long Length, DateTime Modified)> Snapshot(FilerConfig config)
    {
        var folders = config.AllConvertRules.Select(rule => (rule.From, Recurse: false))
            .Concat(config.AllCopyRules.Select(rule => (rule.From, rule.Recurse)))
            .GroupBy(folder => Path.GetFullPath(Path.Combine(config.SourceRoot, folder.From)), StringComparer.OrdinalIgnoreCase)
            .Select(group => (Path: group.Key, Recurse: group.Any(folder => folder.Recurse)));

        var files = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, recurse) in folders)
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", option))
                {
                    files[file.FullName] = (file.Length, file.LastWriteTimeUtc);
                }
            }
            catch (IOException)
            {
                // OneDrive briefly locking a folder; the next pass sees it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return files;
    }

    private static bool SameSnapshot(
        Dictionary<string, (long Length, DateTime Modified)> left,
        Dictionary<string, (long Length, DateTime Modified)> right) =>
        left.Count == right.Count &&
        left.All(entry => right.TryGetValue(entry.Key, out var other) && other == entry.Value);
}
