using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace DocFiler;

/// <summary>
/// --dump-config (hidden): prints the loaded config, person templates expanded, one line per
/// value, so two builds can be diffed. It walks every public property by reflection rather than
/// naming them, so a setting the JSON reader silently failed to bind still shows up as a
/// difference, and a setting added later is covered without anyone remembering to add it here.
/// </summary>
internal static class ConfigDump
{
    public static int Run(FilerConfig config)
    {
        Properties("config", config);
        return 0;
    }

    private static void Properties<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string path, T value)
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            Value($"{path}.{property.Name}", property.GetValue(value));
        }
    }

    private static void Value(string path, object? value)
    {
        switch (value)
        {
            case null:
                Console.WriteLine($"{path} = null");
                break;
            case string text:
                Console.WriteLine($"{path} = \"{text}\"");
                break;
            case bool or int or Converter:
                Console.WriteLine($"{path} = {value}");
                break;
            case List<string> list:
                Console.WriteLine($"{path}.Count = {list.Count}");
                for (var i = 0; i < list.Count; i++)
                {
                    Value($"{path}[{i}]", list[i]);
                }
                break;
            case WatchSettings watch:
                Properties(path, watch);
                break;
            case List<Person> list:
                Items(path, list);
                break;
            case List<ConvertRule> list:
                Items(path, list);
                break;
            case List<CopyRule> list:
                Items(path, list);
                break;
            case List<ArchiveRule> list:
                Items(path, list);
                break;
            case List<SelfTestCase> list:
                Items(path, list);
                break;
            default:
                // Never skip a value quietly: a type this doesn't know is a gap in the comparison.
                throw new InvalidOperationException($"--dump-config doesn't know how to print {path} ({value.GetType().Name})");
        }
    }

    private static void Items<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(string path, List<T> list)
    {
        Console.WriteLine($"{path}.Count = {list.Count}");
        for (var i = 0; i < list.Count; i++)
        {
            Properties($"{path}[{i}]", list[i]);
        }
    }
}
