using System.Text.Json.Nodes;
using PyCompat;
using Tomlyn.Model;

namespace DocFiler;

/// <summary>
/// A TOML config (1.2.0), read into the same model as the JSON one: the file is parsed with PyCompat's strict reader
/// (the one every tool on the shared library uses), turned into a JSON tree, and bound by the same generated
/// serialiser, so the two formats can't drift apart. Keys are written in TOML's usual snake_case and bound to the
/// model's names: <c>source_root</c> is <c>sourceRoot</c>, <c>[[person_convert_templates]]</c> is
/// <c>personConvertTemplates</c>, <c>[[self_test]]</c> is <c>selfTest</c>. A camelCase key works too.
/// </summary>
internal static class TomlConfig
{
    public static bool IsToml(string path) => string.Equals(Path.GetExtension(path), ".toml", StringComparison.OrdinalIgnoreCase);

    /// <summary>The TOML file as the JSON text the config binder reads.</summary>
    public static string ToJson(string text, string source) => Node(PyToml.Read(text, source))!.ToJsonString();

    /// <summary>snake_case to camelCase: "check_every_seconds" is "checkEverySeconds". Other keys are left alone.</summary>
    internal static string Key(string key)
    {
        if (!key.Contains('_'))
        {
            return key;
        }

        var parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? key
            : parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static JsonNode? Node(object? value) => value switch
    {
        null => null,
        TomlTable table => Object(table),
        TomlTableArray array => new JsonArray([.. array.Select(t => (JsonNode?)Object(t))]),
        TomlArray array => new JsonArray([.. array.Select(Node)]),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => JsonValue.Create(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
    };

    private static JsonObject Object(TomlTable table)
    {
        var result = new JsonObject();
        foreach (var (key, value) in table)
        {
            result[Key(key)] = Node(value);
        }

        return result;
    }
}
