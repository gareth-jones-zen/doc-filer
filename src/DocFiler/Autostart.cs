using System.Text.RegularExpressions;
using WinBackground;

namespace DocFiler;

/// <summary>
/// <c>autostart on | off | status</c>: watch mode at every logon, through Windows Task Scheduler (WinBackground's
/// ScheduledTask).
///
/// The task, <c>doc-filer watch</c>, runs as you, only while you're logged on, and needs no admin rights. It catches
/// up a missed start, restarts the watcher if it crashes (three times, five minutes apart), and is never stopped for
/// running too long. It runs <c>doc-filer-background.exe --watch</c> directly: a program with no window, so there's
/// no console to close by accident, and stopping the task stops the watcher itself.
///
/// <para>Turning it on or off also stops any other doc-filer watcher on the same config, wherever it was started
/// from: one started by hand at a console, or one left behind by an older doc-filer, whose task ran it under
/// <c>conhost.exe</c> and PowerShell, so stopping that task stopped only the console host and left the watcher
/// itself running.</para>
/// </summary>
public static partial class Autostart
{
    public const string TaskName = "doc-filer watch";
    public const string ConsoleExe = "doc-filer.exe";
    public const string BackgroundExe = "doc-filer-background.exe";

    // Seams for the tests; the defaults are the real ones.
    internal static Func<string, string, ITask> Tasks { get; set; } =
        (name, description) => new ScheduledTask(name, description);
    internal static Func<string, int> StopOtherWatchers { get; set; } = StopWatchers;
    internal static Func<string, bool> WatcherRunning { get; set; } = config => Locks.Held(RunLock.WatchName(config));
    internal static string ProgramDir { get; set; } = AppContext.BaseDirectory;

    public static int Run(string[] args, TextWriter output, TextWriter errors)
    {
        var verb = args.Length > 0 ? args[0] : "status";
        var rest = args.Length > 1 ? args[1..] : [];
        try
        {
            switch (verb)
            {
                case "on":
                    return On(rest, output);
                case "off":
                    return Off(output);
                case "status":
                    return Status(output);
                default:
                    errors.WriteLine($"Unknown autostart command '{verb}'. Use: autostart on [--config <path>] [--log <path>] | off | status");
                    return 2;
            }
        }
        catch (Exception exception)
        {
            errors.WriteLine(exception.Message);
            return 2;
        }
    }

    /// <summary>The program the task runs (the windowless one beside this program, if it's there) and its arguments.</summary>
    public static (string Exe, List<string> Args) Command(string programDir, string configPath, string logPath)
    {
        var background = Path.Combine(programDir, BackgroundExe);
        var exe = File.Exists(background) ? background : Path.Combine(programDir, ConsoleExe);
        return (exe, ["--watch", "--config", configPath, "--log", logPath]);
    }

    public static string Description(string configPath, string logPath) =>
        $"Files whatever lands in the staging folder (doc-filer --watch). Config: {configPath}. Log: {logPath}";

    /// <summary>The log when --log isn't given.</summary>
    public static string DefaultLog =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "doc-filer", "doc-filer.log");

    private static int On(string[] args, TextWriter output)
    {
        var config = Option(args, "--config");
        config = Cli.FindConfig(config);
        if (!File.Exists(config))
        {
            throw new FileNotFoundException($"Config not found: {config}");
        }

        config = Path.GetFullPath(config);
        var log = Path.GetFullPath(Option(args, "--log") ?? DefaultLog);
        var task = Tasks(TaskName, Description(config, log));

        // Stop what's running first: the task's own watcher (Stop waits for it to exit), then any other watcher on
        // this config, so the new one starts alone.
        task.Stop();
        StopOtherWatchers(config);

        var (exe, taskArgs) = Command(ProgramDir, config, log);
        task.Enable(exe, taskArgs, Path.GetDirectoryName(config));
        output.WriteLine($"Autostart is on: '{TaskName}' runs at every logon, and has started now.");
        output.WriteLine($"  config  {config}");
        output.WriteLine($"  log     {log}");
        return 0;
    }

    private static int Off(TextWriter output)
    {
        var task = Tasks(TaskName, TaskName);
        var status = task.Status();
        if (status is null)
        {
            output.WriteLine("Autostart is already off.");
            return 0;
        }

        task.Disable();
        if (ConfigIn(status.GetValueOrDefault("command", "")) is { } config)
        {
            StopOtherWatchers(config);
        }

        output.WriteLine($"Autostart is off: '{TaskName}' is stopped and removed.");
        return 0;
    }

    private static int Status(TextWriter output)
    {
        var status = Tasks(TaskName, TaskName).Status();
        if (status is null)
        {
            output.WriteLine("Autostart is off.");
            return 0;
        }

        var command = status.GetValueOrDefault("command", "");
        var state = status.GetValueOrDefault("state", "");
        // As before: only worth saying when the task isn't running either. A watcher holds its config's watch lock
        // while it runs (one started by an older doc-filer held none, but then its task showed Running).
        var running = state == "Running" || (ConfigIn(command) is { } config && WatcherRunning(config));
        output.WriteLine($"Autostart is on ('{TaskName}').");
        output.WriteLine($"  state        {state}{(running ? "" : " (the watcher is not running)")}");
        output.WriteLine($"  last run     {status.GetValueOrDefault("last_run", "")}");
        output.WriteLine($"  last result  {DescribeResult(status.GetValueOrDefault("last_result", ""))}");
        output.WriteLine($"  command      {command}");
        return 0;
    }

    /// <summary>The task's last result, in words. ScheduledTask has already turned "never run" into "-" and
    /// "still running" into "running".</summary>
    internal static string DescribeResult(string result) => result switch
    {
        "-" => "never run",
        "running" => "running",
        "0" => "ok",
        "1" => "exit 1 (a file failed to convert)",
        "2" => "exit 2 (a configuration error)",
        "4" => "exit 4 (another watcher was already running)",
        "" => "",
        _ => "exit " + result,
    };

    /// <summary>The --config path in a task's command line, as Shell.ListToCommandLine wrote it (quoted if it has a
    /// space) or as an older doc-filer's task did (always quoted).</summary>
    internal static string? ConfigIn(string commandLine)
    {
        var match = ConfigArgument().Match(commandLine);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>PowerShell: stop every doc-filer watcher (either program, started any way) whose command line names
    /// this config, and wait up to 15 seconds for each to exit. Never this process.</summary>
    internal static string StopWatchersScript(string configPath, int selfId) => $$"""

        $wanted = {{Shell.Quote(configPath)}}
        $found = @(Get-CimInstance Win32_Process -Filter "Name='doc-filer.exe' OR Name='doc-filer-background.exe'" -ErrorAction SilentlyContinue |
          Where-Object { $_.ProcessId -ne {{selfId}} -and $_.CommandLine -and $_.CommandLine.Contains('--watch') -and
                         $_.CommandLine.IndexOf($wanted, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        foreach ($p in $found) {
          Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
          Wait-Process -Id $p.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
        }
        "stopped=$($found.Count)"
        exit 0

        """;

    private static int StopWatchers(string configPath)
    {
        var done = Shell.PowerShell(StopWatchersScript(configPath, Environment.ProcessId));
        var match = Regex.Match(done.StandardOutput, @"stopped=(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex("""--config\s+(?:"(?<quoted>[^"]+)"|(?<plain>\S+))""")]
    private static partial Regex ConfigArgument();
}
