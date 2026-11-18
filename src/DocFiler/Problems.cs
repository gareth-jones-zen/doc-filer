namespace DocFiler;

/// <summary>
/// 1.4.0: the tool raises its own problems, which an after-run hook did until then. With <c>notify = true</c>, a new
/// staging file no rule matches, and a file that couldn't be converted (once its retries are used up, in watch mode),
/// are notified; with <c>[review]</c>, the unmatched files are also raised as one "unmatched" item, for whoever reads
/// the queue to rename them or add a rule. Each unmatched file reaches here once: the watcher remembers them.
/// </summary>
public static class Problems
{
    public static void Report(FilerConfig config, IReadOnlyList<string> unmatched, IReadOnlyList<string> failed,
        Action<string, IReadOnlyList<string>> notify)
    {
        var settings = Path.GetFileName(config.ConfigPath);
        if (unmatched.Count > 0)
        {
            var names = unmatched.Select(Path.GetFileName).OfType<string>().ToList();
            if (config.ReviewSettings is { } review)
            {
                try
                {
                    review.Open().Add(review.Source, "unmatched", new System.Text.Json.Nodes.JsonObject
                    {
                        ["arrived"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                        ["files"] = new System.Text.Json.Nodes.JsonArray([.. names.Select(n => (System.Text.Json.Nodes.JsonNode)n)]),
                        ["summary_needed"] = false,
                        ["dictation"] = new System.Text.Json.Nodes.JsonArray(),
                        ["title"] = string.Join(", ", names),
                    });
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
                {
                    Log.Error($"Couldn't raise the unmatched files in the review queue: {exception.Message}");
                }
            }
            if (config.Notify)
            {
                notify("Staging file matches no rule", [.. names, $"Rename it, or add a rule in {settings}."]);
            }
        }
        if (failed.Count > 0 && config.Notify)
        {
            notify("doc-filer couldn't convert", [.. failed.Select(Path.GetFileName).OfType<string>(), "See the doc-filer log."]);
        }
    }
}
