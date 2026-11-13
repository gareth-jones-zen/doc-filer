namespace DocFiler;

/// <summary>What ships with the tool, beside the exe: pandoc (unpacked from vendor/ by the build), the Lua filters
/// and the stylesheet.</summary>
public static class Bundled
{
    /// <summary>pandoc.exe under pandoc\ beside the exe (pandoc's zip puts it in a pandoc-&lt;version&gt;\ folder,
    /// so it's looked for at any depth), or null if there isn't one.</summary>
    public static string? Pandoc(string? programDir = null)
    {
        var folder = Path.Combine(programDir ?? AppContext.BaseDirectory, "pandoc");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(folder, "pandoc.exe", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
