using System.Reflection;

namespace DocFiler;

/// <summary>
/// The command line, shared by both programs: doc-filer.exe (a console) and doc-filer-background.exe (no window,
/// what the logon task runs). Exit codes: 0 success, 1 a file failed to convert, 2 a configuration error, 3 another
/// run with the same config didn't finish within 10 minutes, 4 another watcher is already running for the config.
/// </summary>
public static class Cli
{
    /// <summary>From the build (Directory.Build.props, or the release's -p:Version from its tag).</summary>
    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static int Run(string[] args)
    {
        // "pdf" is its own command: it renders one markdown file and needs no config.
        if (args.Length > 0 && args[0] == "pdf")
        {
            return Pdf.Run(args[1..]);
        }

        if (args.Length > 0 && args[0] == "autostart")
        {
            return Autostart.Run(args[1..], Console.Out, Console.Error);
        }

        var dryRun = false;
        var selfTest = false;
        var dumpConfig = false;
        var watch = false;
        var hook = false;
        var noHook = false;
        string? configPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run" or "-n":
                    dryRun = true;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "--dump-config":
                    // Hidden: for comparing two builds' reading of the same config. Not in the usage text.
                    dumpConfig = true;
                    break;
                case "--watch":
                    watch = true;
                    break;
                case "--hook":
                    hook = true;
                    break;
                case "--no-hook":
                    noHook = true;
                    break;
                case "--verbose" or "-v":
                    Log.Verbose = true;
                    break;
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--log" when i + 1 < args.Length:
                    Log.FilePath = args[++i];
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Log.FilePath))!);
                    break;
                case "--version":
                    Console.WriteLine(Version);
                    return 0;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        try
        {
            configPath = FindConfig(configPath);

            if (watch)
            {
                // The config's log, if it has one and --log wasn't given. A config that won't load is the watcher's
                // to report (it waits for a fix), so it's only read for the log here.
                try
                {
                    UseConfigLog(FilerConfig.Load(configPath));
                }
                catch (Exception)
                {
                }

                return new Watcher(configPath).Run();
            }

            var config = FilerConfig.Load(configPath);
            UseConfigLog(config);
            hook = !noHook && (hook || config.Hook);

            if (dumpConfig)
            {
                return ConfigDump.Run(config);
            }

            if (selfTest)
            {
                return SelfTest.Run(config);
            }

            Log.Info($"Using config {config.ConfigPath}");
            Log.Info($"Staging folder {config.SourceRoot}");
            Log.Info($"Repository     {config.ResolvedRepoRoot}");
            if (dryRun)
            {
                Log.Info("Dry run — nothing will be written.");
            }

            foreach (var file in DirectoryProcessor.FindUnmatched(config))
            {
                Log.Warn($"No rule matches {file}");
            }

            return Filer.Run(config, dryRun, hook, [], TimeSpan.FromMinutes(10));
        }
        catch (Exception exception)
        {
            Log.Error(exception.Message);
            return 2;
        }
    }

    /// <summary>The config's log, when --log didn't name one.</summary>
    private static void UseConfigLog(FilerConfig config)
    {
        if (Log.FilePath is null && config.ResolvedLog is { } log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            Log.FilePath = log;
        }
    }

    /// <summary>--config, then $DOC_FILER_CONFIG, then doc-filer.toml or doc-filer.json in the current folder.</summary>
    internal static string FindConfig(string? given)
    {
        if (!string.IsNullOrWhiteSpace(given))
        {
            return given;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("DOC_FILER_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        foreach (var name in new[] { "doc-filer.toml", "doc-filer.json" })
        {
            var inCurrentDirectory = Path.Combine(Environment.CurrentDirectory, name);
            if (File.Exists(inCurrentDirectory))
            {
                return inCurrentDirectory;
            }
        }

        throw new FileNotFoundException(
            "No config: pass --config <path>, set DOC_FILER_CONFIG, or run from a folder holding doc-filer.toml (or doc-filer.json).");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            doc-filer: converts documents saved into a staging folder to markdown, and files them
            into a repo by filename rules.

              doc-filer [options]
              doc-filer autostart on [--config <p>] [--log <p>] | off | restart | status
              doc-filer pdf <file.md> [--css <p>] [--image-width <n>] [--output <p>]   (pdf --help for more)

            Options:
                  --config <p>  The config file, TOML or JSON (default: $DOC_FILER_CONFIG, then
                                ./doc-filer.toml, then ./doc-filer.json).
              -n, --dry-run     Report what would be converted and copied, write nothing.
                  --self-test   Check filename routing against the config's selfTest cases and exit.
                  --watch       Stay running, and file whatever lands in staging. Runs the hook.
                  --hook        After a one-off run, run the config's afterRun command and git commit too
                                (the default when the config sets hook = true).
                  --no-hook     Skip them for this run, whatever the config says.
                  --log <p>     Also append the log to this file (default: the config's log, if set).
              -v, --verbose     Show the per-file "unchanged" lines.
                  --version     Show the version.
              -h, --help        Show this message.

            Exit codes: 0 success, 1 a file failed to convert, 2 a configuration error,
            3 another run with the same config didn't finish within 10 minutes,
            4 another watcher is already running for the config.
            """);
    }
}
