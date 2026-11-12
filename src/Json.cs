using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DocFiler;

/// <summary>
/// Every type the tool reads or writes as JSON, generated at build time rather than found by
/// reflection at run time, so the tool can be published with Native AOT and trimmed.
///
/// The options live in <see cref="Json"/>, not on this class, because the attribute can't pass
/// <c>allowIntegerValues: false</c> to the enum converter.
/// </summary>
[JsonSerializable(typeof(FilerConfig))]
[JsonSerializable(typeof(HookReport))]
internal sealed partial class DocFilerJsonContext : JsonSerializerContext;

internal static class Json
{
    /// <summary>
    /// The config file: comments and trailing commas allowed, names matched whatever their case
    /// ("sourceRoot" binds SourceRoot), and an enum must be spelled out ("Pandoc", not 0), as it
    /// was under the reflection serialiser this replaced.
    /// </summary>
    private static readonly JsonSerializerOptions ConfigOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter<Converter>(allowIntegerValues: false) },
        TypeInfoResolver = DocFilerJsonContext.Default,
    };

    /// <summary>
    /// The after-run hook's report: indented, with the default encoder, which writes anything
    /// outside ASCII as \uXXXX. That keeps the hook's stdin pure ASCII whatever the console's
    /// code page, so a name such as "Zoë" reaches the hook intact.
    /// </summary>
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = DocFilerJsonContext.Default,
    };

    public static FilerConfig? ReadConfig(string json) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<FilerConfig>)ConfigOptions.GetTypeInfo(typeof(FilerConfig)));

    public static string WriteReport(HookReport report) =>
        JsonSerializer.Serialize(report, (JsonTypeInfo<HookReport>)ReportOptions.GetTypeInfo(typeof(HookReport)));
}

/// <summary>
/// What the after-run hook gets on stdin. The names and their order are the hook's contract
/// (README.md, "The after-run hook"), so they're spelled out rather than left to a naming policy.
/// </summary>
internal sealed record HookReport(
    [property: JsonPropertyName("config")] string Config,
    [property: JsonPropertyName("repoRoot")] string RepoRoot,
    [property: JsonPropertyName("written")] IReadOnlyList<string> Written,
    [property: JsonPropertyName("moved")] IReadOnlyList<HookMove> Moved,
    [property: JsonPropertyName("failed")] IReadOnlyList<string> Failed,
    [property: JsonPropertyName("unmatched")] IReadOnlyList<string> Unmatched,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>One archive move in the hook report.</summary>
internal sealed record HookMove(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To);
