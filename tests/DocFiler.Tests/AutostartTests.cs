using WinBackground;

namespace DocFiler.Tests;

/// <summary>autostart on | off | status against a fake task. Nothing here touches Task Scheduler or stops a real
/// watcher: the one test that runs the real stop script names a config no process has.</summary>
[Collection("Serial")]
public sealed class AutostartTests : IDisposable
{
    private readonly Func<string, string, ITask> tasks = Autostart.Tasks;
    private readonly Func<string, int> stop = Autostart.StopOtherWatchers;
    private readonly Func<string, bool> running = Autostart.WatcherRunning;
    private readonly string programDir = Autostart.ProgramDir;
    private readonly FakeTask task = new();
    private readonly List<string> stopped = [];
    private readonly Sandbox box = new();

    public AutostartTests()
    {
        Autostart.Tasks = (name, description) =>
        {
            task.Name = name;
            task.Description = description;
            return task;
        };
        Autostart.StopOtherWatchers = config =>
        {
            task.Calls.Add("stop-others");
            stopped.Add(config);
            return 1;
        };
        Autostart.WatcherRunning = _ => false;
        Autostart.ProgramDir = box.Root;
    }

    public void Dispose()
    {
        Autostart.Tasks = tasks;
        Autostart.StopOtherWatchers = stop;
        Autostart.WatcherRunning = running;
        Autostart.ProgramDir = programDir;
        box.Dispose();
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Autostart.Run(args, output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public void On_stops_what_is_running_then_registers_the_windowless_exe()
    {
        File.WriteAllText(Path.Combine(box.Root, Autostart.BackgroundExe), "");
        var log = Path.Combine(box.Root, "logs", "doc-filer.log");

        var (code, output, _) = Run("on", "--config", box.ConfigPath, "--log", log);

        Assert.Equal(0, code);
        Assert.Equal(["stop", "stop-others", "enable"], task.Calls);
        Assert.Equal([box.ConfigPath], stopped);
        Assert.Equal("doc-filer watch", task.Name);
        Assert.Equal($"Files whatever lands in the staging folder (doc-filer --watch). Config: {box.ConfigPath}. Log: {log}", task.Description);
        Assert.Equal(Path.Combine(box.Root, Autostart.BackgroundExe), task.Exe);
        Assert.Equal(["--watch", "--config", box.ConfigPath, "--log", log], task.Args);
        Assert.Equal(box.Root, task.WorkingDir);
        Assert.Equal(
            $"Autostart is on: 'doc-filer watch' runs at every logon, and has started now.\n  config  {box.ConfigPath}\n  log     {log}\n",
            output);
    }

    [Fact]
    public void On_falls_back_to_the_console_exe_and_the_default_log()
    {
        var (code, _, _) = Run("on", "--config", box.ConfigPath);
        Assert.Equal(0, code);
        Assert.Equal(Path.Combine(box.Root, Autostart.ConsoleExe), task.Exe);
        Assert.Equal(Autostart.DefaultLog, task.Args![^1]);
    }

    [Fact]
    public void On_needs_a_config_that_exists()
    {
        var (code, _, errors) = Run("on", "--config", Path.Combine(box.Root, "missing.json"));
        Assert.Equal(2, code);
        Assert.StartsWith("Config not found: ", errors);
        Assert.Empty(task.Calls);
    }

    [Fact]
    public void Off_removes_the_task_and_stops_any_watcher_on_its_config()
    {
        task.Exists = true;
        task.Command = $"C:\\Programs\\doc-filer\\doc-filer-background.exe --watch --config \"{box.ConfigPath}\" --log C:\\logs\\doc-filer.log";

        var (code, output, _) = Run("off");

        Assert.Equal(0, code);
        Assert.Equal(["status", "disable", "stop-others"], task.Calls);
        Assert.Equal([box.ConfigPath], stopped);
        Assert.Equal("Autostart is off: 'doc-filer watch' is stopped and removed.\n", output);
    }

    [Fact]
    public void Off_stops_an_older_builds_watcher_too()
    {
        // An older doc-filer's task ran the watcher under conhost and PowerShell; stopping it left doc-filer.exe running.
        task.Exists = true;
        task.Command = "conhost.exe --headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:\\src\\doc-filer\\run.ps1\" " +
                       "--watch --config \"C:\\My Repo\\tools\\doc-filer.json\" --log \"C:\\logs\\doc-filer.log\"";
        Run("off");
        Assert.Equal([@"C:\My Repo\tools\doc-filer.json"], stopped);
    }

    [Fact]
    public void Off_when_already_off()
    {
        var (code, output, _) = Run("off");
        Assert.Equal(0, code);
        Assert.Equal("Autostart is already off.\n", output);
        Assert.Equal(["status"], task.Calls);
    }

    [Fact]
    public void Status_says_whether_the_watcher_is_running()
    {
        Assert.Equal("Autostart is off.\n", Run("status").Out);

        task.Exists = true;
        task.State = "Ready";
        task.Result = "4";
        task.Command = $"C:\\doc-filer-background.exe --watch --config C:\\cfg\\doc-filer.json --log C:\\l.log";
        Assert.Equal(
            "Autostart is on ('doc-filer watch').\n" +
            "  state        Ready (the watcher is not running)\n" +
            "  last run     11/13/2026 09:00:00\n" +
            "  last result  exit 4 (another watcher was already running)\n" +
            $"  command      {task.Command}\n",
            Run("status").Out);

        string? asked = null;
        Autostart.WatcherRunning = config => { asked = config; return true; };
        Assert.Contains("  state        Ready\n", Run().Out);   // status is the default
        Assert.Equal(@"C:\cfg\doc-filer.json", asked);
    }

    [Theory]
    [InlineData("-", "never run")]
    [InlineData("running", "running")]
    [InlineData("0", "ok")]
    [InlineData("1", "exit 1 (a file failed to convert)")]
    [InlineData("3221225786", "exit 3221225786")]
    public void Results_in_words(string result, string words) => Assert.Equal(words, Autostart.DescribeResult(result));

    [Fact]
    public void Unknown_command()
    {
        var (code, _, errors) = Run("sideways");
        Assert.Equal(2, code);
        Assert.Contains("Unknown autostart command 'sideways'", errors);
    }

    [Fact]
    public void The_stop_script_finds_either_program_by_its_config_and_never_this_process()
    {
        var script = Autostart.StopWatchersScript(@"C:\Users\O’Brien\doc-filer.json", 1234);
        Assert.Contains(@"$wanted = 'C:\Users\O’’Brien\doc-filer.json'", script);
        Assert.Contains("Name='doc-filer.exe' OR Name='doc-filer-background.exe'", script);
        Assert.Contains("$_.ProcessId -ne 1234", script);
        Assert.Contains("Contains('--watch')", script);
    }

    [Fact]
    public void The_real_stop_script_runs_and_finds_nothing_for_an_unused_config()
    {
        Autostart.StopOtherWatchers = stop;   // the real one, on a config no process has
        Assert.Equal(0, Autostart.StopOtherWatchers(Path.Combine(box.Root, $"{Guid.NewGuid():N}.json")));
    }

    private sealed class FakeTask : ITask
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Calls { get; } = [];
        public bool Exists { get; set; }
        public string State { get; set; } = "Running";
        public string Result { get; set; } = "running";
        public string Command { get; set; } = "";
        public string? Exe { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }
        public string? WorkingDir { get; private set; }

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir)
        {
            Calls.Add("enable");
            (Exe, Args, WorkingDir) = (exe, args, workingDir);
            return exe;
        }

        public bool Disable()
        {
            Calls.Add("disable");
            return Exists;
        }

        public bool Stop()
        {
            Calls.Add("stop");
            return Exists;
        }

        public IReadOnlyDictionary<string, string>? Status()
        {
            Calls.Add("status");
            return Exists
                ? new Dictionary<string, string>
                {
                    ["state"] = State, ["command"] = Command, ["last_run"] = "11/13/2026 09:00:00", ["last_result"] = Result,
                }
                : null;
        }
    }
}
