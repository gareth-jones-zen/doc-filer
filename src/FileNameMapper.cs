using System.Text.RegularExpressions;

namespace DocFiler;

/// <summary>
/// Works out where a staging file should end up and what it should be called.
///
/// The previous implementation read the date positionally from the end of the filename
/// (substring(length - 10, 4) for the year), which meant any suffix broke it:
/// "BW 1-2-1 2026-08-19-part1" produced "8-19-pa-t1.md". Matching by regex instead means
/// the -part&lt;n&gt; convention works, and the meeting label can be pulled out of the same match.
/// </summary>
public static class FileNameMapper
{
    public sealed record Mapping(string Folder, string FileName);

    public static Mapping? Map(ConvertRule rule, string fileNameWithoutExtension)
    {
        // Case-insensitive on purpose. Staging filenames are typed by hand and the casing
        // drifts — "AB Performance review prep 2026-09" against "AB performance review prep
        // 2026-09". Matching case-sensitively silently ignored the file, which is the worst
        // possible failure for an ingest tool: no error, no output, and nothing to notice.
        var match = Regex.Match(fileNameWithoutExtension, rule.Match, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var date = Group(match, "date");
        var part = Group(match, "part");
        var rawLabel = Group(match, "label").Trim();

        var label = rawLabel.Replace(" ", "_");
        var slug = Slugify(rawLabel);

        var fileName = Expand(rule.Name, date, label, slug, part).Trim() + ".md";
        var folder = Expand(rule.To, date, label, slug, part);

        return new Mapping(folder, fileName);
    }

    private static string Group(Match match, string name) =>
        match.Groups[name].Success ? match.Groups[name].Value : string.Empty;

    private static string Expand(string template, string date, string label, string slug, string part) =>
        // {label-slug} first: replacing {label} would otherwise leave a stray "-slug}".
        template.Replace("{label-slug}", slug)
                .Replace("{label}", label)
                .Replace("{date}", date)
                .Replace("{part}", part);

    /// <summary>
    /// "Jane Smith" -> "jane-smith", "Leads Catch-up" -> "leads-catch-up". Lets staging files be
    /// named naturally while folders keep the kebab-case convention used elsewhere in the target repo.
    /// </summary>
    public static string Slugify(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-");
        return slug.Trim('-');
    }
}
