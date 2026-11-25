using ActivityJournal;

namespace DocFiler;

/// <summary>
/// The shared activity journal (1.7.0): one line for each file a run filed into the repo or archived, written through
/// background-tools-lib's ActivityJournal to journal_file (by convention %EA_LOCAL%\journal.jsonl), for a "while you
/// were away" view. Only what really happened: never a dry run. Writing it never fails the run; a problem is logged.
/// <para><c>what</c> is <c>file</c> (copied into the repo: new, or replacing an older copy) or <c>archive</c> (moved into
/// its folder's archive). No <c>undo</c>: either would be done again by the next run while the rules and the staging
/// file are as they are.</para>
/// </summary>
internal static class Activity
{
    public const string Tool = "doc-filer";

    /// <summary>A line for each file filed and archived; nothing without journal_file. Never throws.</summary>
    public static void Record(FilerConfig config, ProcessResult result, string by)
    {
        if (config.ResolvedJournal is not { } path || (result.Filed.Count == 0 && result.Moved.Count == 0))
        {
            return;
        }

        try
        {
            var journal = new Journal(path);
            void Problem(string message) => Log.Warn($"journal: {message}");

            foreach (var filed in result.Filed)
            {
                journal.TryWrite(new JournalEntry
                {
                    Tool = Tool,
                    What = "file",
                    By = by,
                    Subject = filed.Path,
                    Why = filed.Replaced ? "replaced the older copy: the staging file changed" : "new",
                    Sensitivity = filed.Sensitivity,
                }, Problem);
            }

            foreach (var moved in result.Moved)
            {
                var rule = moved.Rule >= 0 && moved.Rule < config.Archive.Count ? config.Archive[moved.Rule] : null;
                journal.TryWrite(new JournalEntry
                {
                    Tool = Tool,
                    What = "archive",
                    By = by,
                    // Both paths in the subject, which a screen shown to others hides: a file's name can name a person.
                    Subject = $"{moved.From} -> {moved.To}",
                    Rule = rule?.Folder,
                    Why = rule is null ? null : $"dated more than {rule.KeepDays} days ago",
                    Sensitivity = rule?.Sensitivity ?? Sensitivity.Private,
                }, Problem);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Warn($"journal: couldn't write to {path}: {error.GetType().Name}: {error.Message}");
        }
    }
}
