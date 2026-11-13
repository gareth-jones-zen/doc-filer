using System.Text.Json;

namespace DocFiler.Tests;

/// <summary>Tests that touch process-wide state (the static Log, the console, Autostart's seams) run one at a time.</summary>
[CollectionDefinition("Serial", DisableParallelization = true)]
public sealed class SerialCollection;

/// <summary>A throwaway staging folder, repo and config. Synthetic names only.</summary>
public sealed class Sandbox : IDisposable
{
    public Sandbox(string? archive = null, IEnumerable<string>? afterRun = null)
    {
        Root = Directory.CreateTempSubdirectory("doc-filer-tests-").FullName;
        Staging = Path.Combine(Root, "staging");
        Repo = Path.Combine(Root, "repo");
        State = Path.Combine(Root, "state");
        Directory.CreateDirectory(Path.Combine(Staging, "Notes"));
        Directory.CreateDirectory(Repo);
        ConfigPath = Path.Combine(Root, "doc-filer.json");
        WriteConfig(settleSeconds: 60, archive: archive, afterRun: afterRun);
    }

    public string Root { get; }
    public string Staging { get; }
    public string Repo { get; }
    public string State { get; }
    public string ConfigPath { get; }

    public void WriteConfig(int settleSeconds, string? archive = null, IEnumerable<string>? afterRun = null)
    {
        var hook = JsonSerializer.Serialize(afterRun?.ToArray() ?? [], SupportJson.Default.StringArray);
        var staging = JsonSerializer.Serialize(Staging, SupportJson.Default.String);
        File.WriteAllText(ConfigPath, $$"""
            {
              // a test config
              "sourceRoot": {{staging}},
              "repoRoot": "repo",
              "afterRun": {{hook}},
              "watch": { "checkEverySeconds": 30, "settleSeconds": {{settleSeconds}} },
              {{(archive is null ? "" : $"\"archive\": [{archive}],")}}
              "convert": [
                { "from": "Notes", "match": "^Note (?<date>\\d{4}-\\d{2}-\\d{2})$", "extensions": [".txt"],
                  "to": "markdown", "name": "{date}", "converter": "Copy" }
              ],
              "copy": [ { "from": "Notes/markdown", "extensions": [".md", ".txt"], "to": "out" } ]
            }
            """);
    }

    public string AddStaging(string name, string text = "hello")
    {
        var path = Path.Combine(Staging, "Notes", name);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class SupportJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Points the static Log at a file for one test, and back afterwards.</summary>
public sealed class LogCapture : IDisposable
{
    private readonly string? previous = Log.FilePath;

    public LogCapture(string folder)
    {
        Path = System.IO.Path.Combine(folder, "test.log");
        Log.FilePath = Path;
    }

    public string Path { get; }

    public string[] Lines => File.Exists(Path) ? File.ReadAllLines(Path) : [];

    public void Dispose() => Log.FilePath = previous;
}
