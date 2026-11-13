using System.Reflection;

namespace DocFiler.Tests;

[Collection("Serial")]
public sealed class CliTests
{
    private static (int Code, string Out) Run(params string[] args)
    {
        var before = Console.Out;
        var output = new StringWriter { NewLine = "\n" };
        Console.SetOut(output);
        try
        {
            return (Cli.Run(args), output.ToString());
        }
        finally
        {
            Console.SetOut(before);
        }
    }

    [Fact]
    public void Version_is_the_builds()
    {
        var built = typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.False(string.IsNullOrEmpty(built));
        Assert.Equal(built, Cli.Version);
        Assert.Equal((0, built + "\n"), Run("--version"));
    }

    [Fact]
    public void The_example_configs_self_test_passes()
    {
        using var box = new Sandbox();
        var example = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "doc-filer.example.json"));
        var config = Path.Combine(box.Root, "example.json");
        File.WriteAllText(config, example.Replace(@"C:\\Users\\you\\OneDrive\\Staging", box.Staging.Replace(@"\", @"\\"))
            .Replace("\"repoRoot\": \"..\"", "\"repoRoot\": \"repo\""));

        var (code, output) = Run("--self-test", "--config", config);

        Assert.Equal(0, code);
        Assert.Contains("filename cases passed.", output);
        Assert.DoesNotContain("FAIL", output);
    }

    [Fact]
    public void An_unknown_argument_is_exit_2()
    {
        Assert.Equal(2, Run("--sideways").Code);
    }

    [Fact]
    public void Usage_names_the_exe_not_a_script()
    {
        var (code, output) = Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("doc-filer autostart on", output);
        Assert.DoesNotContain("run.ps1", output);
    }

    [Fact]
    public void The_bundled_pandoc_is_found_beside_the_exe_at_any_depth()
    {
        using var box = new Sandbox();
        Assert.Null(Bundled.Pandoc(box.Root));
        var inner = Directory.CreateDirectory(Path.Combine(box.Root, "pandoc", "pandoc-3.13")).FullName;
        File.WriteAllText(Path.Combine(inner, "pandoc.exe"), "");
        Assert.Equal(Path.Combine(inner, "pandoc.exe"), Bundled.Pandoc(box.Root));
    }
}
