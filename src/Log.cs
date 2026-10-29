namespace DocFiler;

public static class Log
{
    /// <summary>Also append every line here (watch mode runs with no console to read).</summary>
    public static string? FilePath { get; set; }

    /// <summary>Show the per-file "Skipping … unchanged" lines. Hundreds per run, so off by default.</summary>
    public static bool Verbose { get; set; }

    private static readonly Lock Gate = new();

    // MM, not mm: the original format string logged the minute as the month ("2026/32/26").
    private static string Timestamp => DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");

    public static void Info(string message) => Write($"{Timestamp} {message}");

    public static void Detail(string message)
    {
        if (Verbose)
        {
            Info(message);
        }
    }

    public static void Plan(string message) => Write($"{Timestamp} [dry-run] {message}");

    public static void Warn(string message) => Write($"{Timestamp} WARNING: {message}");

    public static void Error(string message) => Write($"{Timestamp} ERROR: {message}", error: true);

    private static void Write(string line, bool error = false)
    {
        lock (Gate)
        {
            (error ? Console.Error : Console.Out).WriteLine(line);
            if (FilePath is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // A log line is never worth failing a run over.
            }
        }
    }
}
