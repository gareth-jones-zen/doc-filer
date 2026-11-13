namespace DocFiler;

/// <summary>
/// Checks filename routing against the real config without touching the filesystem.
/// Cheap insurance for the cases that are easy to get wrong and expensive to notice:
/// split recordings, topic labels, and series whose names contain another rule's marker
/// word. The cases live in the config ("selfTest"), beside the rules they test.
/// Run with --self-test.
/// </summary>
public static class SelfTest
{
    public static int Run(FilerConfig config)
    {
        if (config.SelfTest.Count == 0)
        {
            Console.WriteLine("No selfTest cases in the config.");
            return 0;
        }

        var failures = 0;

        foreach (var testCase in config.SelfTest)
        {
            var extension = Path.GetExtension(testCase.File);
            var fileName = Path.GetFileNameWithoutExtension(testCase.File);
            var matches = config.AllConvertRules
                .Where(rule => NormalisePath(rule.From) == NormalisePath(testCase.From))
                .Where(rule => rule.Extensions.Any(candidate =>
                    string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)))
                .Select(rule => new { rule, mapping = FileNameMapper.Map(rule, fileName) })
                .Where(result => result.mapping is not null)
                .ToList();

            var name = $"{testCase.From}/{testCase.File}";
            var why = string.IsNullOrWhiteSpace(testCase.Why) ? "" : $"{Environment.NewLine}        why       {testCase.Why}";

            if (matches.Count == 0)
            {
                Console.WriteLine($"FAIL  {name} — no rule matched{why}");
                failures++;
                continue;
            }

            if (matches.Count > 1)
            {
                var rules = string.Join(", ", matches.Select(result => result.rule.Match));
                Console.WriteLine($"FAIL  {name} — matched {matches.Count} rules: {rules}{why}");
                failures++;
                continue;
            }

            var mapping = matches[0].mapping!;
            var actualFolder = NormalisePath(mapping.Folder);
            var expectedFolder = NormalisePath(testCase.Folder);

            if (actualFolder != expectedFolder || mapping.FileName != testCase.Name)
            {
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"        expected  {expectedFolder}/{testCase.Name}");
                Console.WriteLine($"        actual    {actualFolder}/{mapping.FileName}{why}");
                failures++;
                continue;
            }

            Console.WriteLine($"ok    {name} -> {actualFolder}/{mapping.FileName}");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"All {config.SelfTest.Count} filename cases passed."
            : $"{failures} of {config.SelfTest.Count} filename cases FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static string NormalisePath(string path) => path.Replace('\\', '/').TrimEnd('/');
}
