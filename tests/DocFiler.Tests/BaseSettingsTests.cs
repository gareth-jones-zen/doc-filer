namespace DocFiler.Tests;

/// <summary>A TOML config can name a base file of settings shared with other tools (1.3.0): its [paths] are variables
/// for the config's paths, its settings doc-filer knows fill in what the config doesn't set, and a table such as [git]
/// applies only where the config has the section too.</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "doc-filer-base-" + Guid.NewGuid().ToString("N"));
    private readonly string staging;

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "tools"));
        Directory.CreateDirectory(Path.Combine(dir, "repo"));
        staging = Path.Combine(dir, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(dir, "tools", "base-config.toml"), $"""
            hook = true
            log = '%SHARED%/base.log'
            tenant = "not one of doc-filer's settings"

            [paths]
            SHARED = '{dir}/shared'
            LOGS = '%SHARED%/logs'

            [git]
            repo = '../repo'
            push = true
            trailers = ['Co-Authored-By: Someone <someone@example.com>']
            """);
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

    private FilerConfig Load(string text)
    {
        var path = Path.Combine(dir, "tools", "doc-filer.toml");
        File.WriteAllText(path, $"source_root = '{staging}'\nrepo_root = '../repo'\n" + text);
        return FilerConfig.Load(path);
    }

    [Fact]
    public void Known_settings_and_path_variables_come_from_the_base()
    {
        var config = Load("base = 'base-config.toml'\n");
        Assert.EndsWith("base-config.toml", config.BasePath);
        Assert.True(config.Hook);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "shared", "base.log")), config.ResolvedLog);
    }

    [Fact]
    public void The_configs_own_settings_win_and_use_the_bases_paths()
    {
        var config = Load("base = 'base-config.toml'\nhook = false\nlog = '%LOGS%/doc-filer.log'\n");
        Assert.False(config.Hook);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "shared", "logs", "doc-filer.log")), config.ResolvedLog);
    }

    [Fact]
    public void Path_variables_fall_back_to_the_environment()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE")!;
        var config = Load("base = 'base-config.toml'\nlog = '%USERPROFILE%/x/%NOT_A_VARIABLE_ANYWHERE%.log'\n");
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "x", "%NOT_A_VARIABLE_ANYWHERE%.log")), config.ResolvedLog);
    }

    [Fact]
    public void Git_applies_only_where_the_config_has_the_section()
    {
        Assert.Null(Load("base = 'base-config.toml'\n").GitSettings);

        var config = Load("base = 'base-config.toml'\n[git]\npush = false\n");
        Assert.NotNull(config.GitSettings);
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "repo")), Path.GetFullPath(config.GitSettings!.Repo));
        Assert.False(config.GitSettings.Push);
        Assert.Equal(["Co-Authored-By: Someone <someone@example.com>"], config.GitSettings.Trailers);
    }

    [Fact]
    public void Without_a_base_nothing_changes()
    {
        var config = Load("log = 'x.log'\n");
        Assert.Null(config.BasePath);
        Assert.False(config.Hook);
        Assert.Null(config.GitSettings);
        Assert.Equal(Path.Combine(dir, "tools", "x.log"), config.ResolvedLog);
    }

    [Fact]
    public void A_missing_base_is_a_config_problem()
    {
        var error = Assert.Throws<InvalidDataException>(() => Load("base = 'nope.toml'\n"));
        Assert.Contains("doesn't exist", error.Message);
        Assert.Contains("nope.toml", error.Message);
    }
}
