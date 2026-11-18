namespace DocFiler.Tests;

/// <summary>A TOML config (1.2.0) binds to the same model as the JSON one.</summary>
public sealed class TomlConfigTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "doc-filer-toml-" + Guid.NewGuid().ToString("N"));

    public TomlConfigTests() => Directory.CreateDirectory(dir);

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

    [Theory]
    [InlineData("source_root", "sourceRoot")]
    [InlineData("person_convert_templates", "personConvertTemplates")]
    [InlineData("self_test", "selfTest")]
    [InlineData("check_every_seconds", "checkEverySeconds")]
    [InlineData("from", "from")]
    [InlineData("luaFilter", "luaFilter")]
    public void Snake_case_keys_bind_to_the_models_names(string key, string expected) =>
        Assert.Equal(expected, TomlConfig.Key(key));

    [Fact]
    public void The_same_settings_load_the_same_from_toml_and_json()
    {
        var json = Path.Combine(dir, "doc-filer.json");
        File.WriteAllText(json, """
            {
              "sourceRoot": "STAGING_JSON",
              "repoRoot": "repo",
              "afterRun": ["python", "hook.py"],
              "watch": { "checkEverySeconds": 10, "settleSeconds": 20 },
              "people": [ { "initials": "BW", "destination": "team/ben" } ],
              "personConvertTemplates": [ { "from": "Notes", "match": "^{initials} 1-2-1 (?<date>\\d{4}-\\d{2}-\\d{2})$", "extensions": [".docx"], "to": "markdown/{initials}", "converter": "Pandoc" } ],
              "convert": [ { "from": "Weekly", "match": "^SMT (?<date>\\d{4}-\\d{2}-\\d{2})$", "extensions": [".html"], "to": "md", "converter": "Pandoc", "inputFormat": "html", "luaFilter": "onenote-clean.lua", "prettify": false } ],
              "copy": [ { "from": "Notes/markdown", "extensions": [".md"], "to": "meetings", "recurse": true } ],
              "archive": [ { "folder": "preps", "keepDays": 7 } ],
              "selfTest": [ { "from": "Notes", "file": "BW 1-2-1 2026-08-19.docx", "folder": "markdown/BW", "name": "2026-08-19.md" } ]
            }
            """);
        var toml = Path.Combine(dir, "doc-filer.toml");
        File.WriteAllText(toml, """
            source_root = 'STAGING_TOML'
            repo_root = 'repo'
            after_run = ['python', 'hook.py']
            log = 'logs/doc-filer.log'
            hook = true

            [watch]
            check_every_seconds = 10
            settle_seconds = 20

            [[people]]
            initials = 'BW'
            destination = 'team/ben'

            [[person_convert_templates]]
            from = 'Notes'
            match = '^{initials} 1-2-1 (?<date>\d{4}-\d{2}-\d{2})$'
            extensions = ['.docx']
            to = 'markdown/{initials}'
            converter = 'Pandoc'

            [[convert]]
            from = 'Weekly'
            match = '^SMT (?<date>\d{4}-\d{2}-\d{2})$'
            extensions = ['.html']
            to = 'md'
            converter = 'Pandoc'
            input_format = 'html'
            lua_filter = 'onenote-clean.lua'
            prettify = false

            [[copy]]
            from = 'Notes/markdown'
            extensions = ['.md']
            to = 'meetings'
            recurse = true

            [[archive]]
            folder = 'preps'
            keep_days = 7

            [[self_test]]
            from = 'Notes'
            file = 'BW 1-2-1 2026-08-19.docx'
            folder = 'markdown/BW'
            name = '2026-08-19.md'
            """);

        var staging = Path.Combine(dir, "staging");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.Combine(dir, "repo"));
        File.WriteAllText(json, File.ReadAllText(json).Replace("STAGING_JSON", staging.Replace("\\", "\\\\")));
        File.WriteAllText(toml, File.ReadAllText(toml).Replace("STAGING_TOML", staging));

        var a = FilerConfig.Load(json);
        var b = FilerConfig.Load(toml);
        Assert.Equal(a.SourceRoot, b.SourceRoot);
        Assert.Equal(a.ResolvedRepoRoot, b.ResolvedRepoRoot);
        Assert.Equal(a.AfterRunCommands, b.AfterRunCommands);
        Assert.Equal((a.Watch.CheckEverySeconds, a.Watch.SettleSeconds), (b.Watch.CheckEverySeconds, b.Watch.SettleSeconds));
        Assert.Equal(a.AllConvertRules.Select(r => (r.From, r.Match, r.To, r.Converter, r.InputFormat, r.LuaFilter, r.Prettify)),
            b.AllConvertRules.Select(r => (r.From, r.Match, r.To, r.Converter, r.InputFormat, r.LuaFilter, r.Prettify)));
        Assert.Equal(a.AllCopyRules.Select(r => (r.From, r.To, r.Recurse)), b.AllCopyRules.Select(r => (r.From, r.To, r.Recurse)));
        Assert.Equal(a.Archive.Single().KeepDays, b.Archive.Single().KeepDays);
        Assert.Equal(a.SelfTest.Single().Name, b.SelfTest.Single().Name);
        Assert.False(a.Hook);
        Assert.Null(a.ResolvedLog);
        Assert.True(b.Hook);
        Assert.Equal(Path.Combine(dir, "logs", "doc-filer.log"), b.ResolvedLog);
    }

    [Fact]
    public void A_broken_toml_file_is_refused_with_where()
    {
        var toml = Path.Combine(dir, "doc-filer.toml");
        File.WriteAllText(toml, "source_root = 'x'\nsource_root = 'y'\n");
        var error = Assert.ThrowsAny<Exception>(() => FilerConfig.Load(toml));
        Assert.Contains("doc-filer.toml", error.Message);
    }
}
