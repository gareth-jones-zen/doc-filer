using DocFiler;

var dryRun = false;
var selfTest = false;
var watch = false;
var hook = false;
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
        case "--watch":
            watch = true;
            break;
        case "--hook":
            hook = true;
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
    configPath ??= FindConfig();

    if (watch)
    {
        return new Watcher(configPath).Run();
    }

    var config = FilerConfig.Load(configPath);

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

static string FindConfig()
{
    var fromEnvironment = Environment.GetEnvironmentVariable("DOC_FILER_CONFIG");
    if (!string.IsNullOrWhiteSpace(fromEnvironment))
    {
        return fromEnvironment;
    }

    var inCurrentDirectory = Path.Combine(Environment.CurrentDirectory, "doc-filer.json");
    if (File.Exists(inCurrentDirectory))
    {
        return inCurrentDirectory;
    }

    throw new FileNotFoundException(
        "No config: pass --config <path>, set DOC_FILER_CONFIG, or run from a folder holding doc-filer.json.");
}

static void PrintUsage()
{
    Console.WriteLine("""
        doc-filer: converts documents saved into a staging folder to markdown, and files them
        into a repo by filename rules.

          run.ps1 [options]

        Options:
              --config <p>  The config file (default: $DOC_FILER_CONFIG, then ./doc-filer.json).
          -n, --dry-run     Report what would be converted and copied, write nothing.
              --self-test   Check filename routing against the config's selfTest cases and exit.
              --watch       Stay running, and file whatever lands in staging. Runs the hook.
              --hook        After a one-off run, run the config's afterRun command too.
              --log <p>     Also append the log to this file.
          -v, --verbose     Show the per-file "unchanged" lines.
          -h, --help        Show this message.

        Exit codes: 0 success, 1 a file failed to convert, 2 a configuration error,
        3 another run with the same config didn't finish within 10 minutes.
        """);
}
