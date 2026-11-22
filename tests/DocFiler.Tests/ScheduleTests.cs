using WinBackground;

namespace DocFiler.Tests;

/// <summary>[schedule], background-tools-lib's shared scheduling settings (1.6.0): doc-filer is a watcher, so only hours,
/// days and end_date apply (when it files); it polls in seconds, [watch] check_every_seconds.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "doc-filer-schedule-" + Guid.NewGuid().ToString("N"));

    public ScheduleTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "repo"));
        Directory.CreateDirectory(Path.Combine(dir, "staging"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private FilerConfig Load(string text, string name = "doc-filer.toml")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, $"source_root = '{Path.Combine(dir, "staging")}'\nrepo_root = 'repo'\n" + text);
        return FilerConfig.Load(path);
    }

    [Fact]
    public void With_no_schedule_it_always_files_as_before()
    {
        var config = Load("[watch]\ncheck_every_seconds = 30\nsettle_seconds = 60\n");
        Assert.Equal(30, config.Watch.CheckEverySeconds);
        Assert.Equal(60, config.Watch.SettleSeconds);
        Assert.True(config.ResolvedSchedule.IsActive(new DateTime(2026, 11, 14, 3, 0, 0)));
        Assert.Equal("v1 watcher logon=yes", config.ResolvedSchedule.Fingerprint(TaskKind.Watcher));
    }

    [Fact]
    public void Hours_and_days_limit_when_it_files()
    {
        var config = Load("[watch]\ncheck_every_seconds = 30\n\n[schedule]\nhours = \"07:00-19:00\"\ndays = \"Mon-Fri\"\nend_date = \"2027-01-01\"\n");
        Assert.True(config.ResolvedSchedule.IsActive(new DateTime(2026, 11, 9, 12, 0, 0)));
        Assert.False(config.ResolvedSchedule.IsActive(new DateTime(2026, 11, 9, 20, 0, 0)));
        Assert.False(config.ResolvedSchedule.IsActive(new DateTime(2026, 11, 14, 12, 0, 0)));
        Assert.False(config.ResolvedSchedule.IsActive(new DateTime(2027, 1, 4, 12, 0, 0)));
    }

    [Fact]
    public void Every_minutes_is_refused_with_a_pointer_to_check_every_seconds()
    {
        var error = Assert.Throws<InvalidDataException>(() => Load("[schedule]\nevery_minutes = 5\n"));
        Assert.Contains("check_every_seconds", error.Message);
    }

    [Fact]
    public void A_bad_or_unknown_schedule_setting_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Load("[schedule]\nhourz = \"07:00-19:00\"\n", "a.toml"));
        Assert.Throws<InvalidDataException>(() => Load("[schedule]\nafter_unlock_minutes = 1\n", "b.toml"));
        Assert.Throws<InvalidDataException>(() => Load("[schedule]\nhours = \"7am\"\n", "c.toml"));
    }
}
