using System.ComponentModel;
using RepoCommit;

namespace DocFiler;

/// <summary>
/// The config's <c>git</c>: commits what a run filed into the repo, and nothing else.
///
/// <list type="bullet">
/// <item><b>Archive moves first</b>, one commit per archive rule, as renames, so <c>git log --follow</c> keeps a file's
/// history. Only moves of files git tracked are committed; a file that was never committed moves but stays
/// uncommitted. The subject is the rule's <c>commitMessage</c> ("Archive {count} …").</item>
/// <item><b>Then the files written</b>, of those only the ones git sees as changed, as "Ingest {folder} {name}, …" in
/// git status's order.</item>
/// <item>Each message ends with where it came from and the config's trailers. A commit that fails is logged and
/// notified ("Archived, not committed", "Filed, not committed"); the files stay where they were filed. A push that
/// fails is only logged.</item>
/// <item><b>git not set up</b> (not installed, the repo folder not a repository, or a folder inside one rather than its
/// top) commits nothing and notifies nothing: one log line a process.</item>
/// </list>
/// </summary>
public static class RepoCommits
{
    /// <summary>An archive commit's subject when the rule has no commitMessage.</summary>
    public const string DefaultArchiveMessage = "Archive {count} file(s) from {folder} older than {keepDays} days";

    /// <summary>The placeholders an archive rule's commitMessage may use.</summary>
    public static readonly IReadOnlyList<string> ArchivePlaceholders = ["{count}", "{folder}", "{keepDays}"];

    /// <summary>"one-to-ones 2026-10-20": a file's folder and its name without the extension, as a commit names it.</summary>
    public static string Named(string repoPath)
    {
        var parts = repoPath.Split('/');
        var name = parts[^1];
        var dot = name.LastIndexOf('.');
        return $"{(parts.Length > 1 ? parts[^2] : "")} {(dot > 0 && dot < name.Length - 1 ? name[..dot] : name)}";
    }

    public static string ArchiveSubject(ArchiveRule rule, int count) =>
        (string.IsNullOrEmpty(rule.CommitMessage) ? DefaultArchiveMessage : rule.CommitMessage)
        .Replace("{count}", count.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{folder}", rule.Folder)
        .Replace("{keepDays}", rule.KeepDays.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Commit a run's moves and files. Does nothing without a git section (or with commit set to false).</summary>
    public static void Run(FilerConfig config, ProcessResult result, Action<string, IReadOnlyList<string>> notify)
    {
        if (config.GitSettings is not { Commit: true } git || (result.Written.Count == 0 && result.Moved.Count == 0))
        {
            return;
        }

        var committer = git.Open(Log.Info);
        if (committer.Skipped is not null)
        {
            // git isn't set up here (not installed, not a repository, or a folder inside one): the files are filed, and
            // the library has said so in the log, once a process. Nothing to notify, whatever notify says.
            return;
        }

        var see =$"See {(Log.FilePath is { } file ? Path.GetFileName(file) : "the log")}.";
        string settings;
        try
        {
            settings = committer.RepoPath(config.ConfigPath) ?? Path.GetFileName(config.ConfigPath);
        }
        catch (InvalidOperationException error)
        {
            Log.Error($"git: {error.Message}");
            notify("Filed, not committed", [error.Message, see]);
            return;
        }

        foreach (var group in result.Moved.GroupBy(move => move.Rule).OrderBy(group => group.Key))
        {
            var rule = group.Key >= 0 && group.Key < config.Archive.Count ? config.Archive[group.Key] : new ArchiveRule();
            var subject = "";
            try
            {
                // Moves are reported relative to repoRoot; git wants them relative to its own top.
                var moves = group.Select(move => (Move: move, From: committer.RepoPath(Path.Combine(config.ResolvedRepoRoot, move.From)),
                    To: committer.RepoPath(Path.Combine(config.ResolvedRepoRoot, move.To)))).ToList();
                var tracked = committer.Tracked(moves.Where(m => m.From is not null).Select(m => m.From!));
                foreach (var m in moves.Where(m => m.From is null || m.To is null || !tracked.Contains(m.From)))
                {
                    Log.Info($"Not committing the archive of {m.Move.From}: it was never committed");
                }

                var pairs = moves.Where(m => m.From is not null && m.To is not null && tracked.Contains(m.From))
                    .Select(m => new Rename(m.From!, m.To!)).ToList();
                if (pairs.Count == 0)
                {
                    continue;
                }

                subject = ArchiveSubject(rule, pairs.Count);
                var done = committer.Commit(Committer.Message(subject, $"Moved by doc-filer's archive rule ({settings})."), [], pairs,
                    git.Trailers, git.Push);
                if (done.Error is not null)
                {
                    notify("Archived, not committed", [Unarchived(subject, rule, group.Count()), see]);
                }
            }
            catch (Exception error) when (error is GitException or InvalidOperationException or Win32Exception or TimeoutException or IOException)
            {
                Log.Error($"git: archive commit failed: {error.Message}");
                notify("Archived, not committed", [Unarchived(subject, rule, group.Count()), see]);
            }
        }

        var names = "";
        try
        {
            var paths = committer.Changed(result.Written.Select(path => Path.Combine(config.ResolvedRepoRoot, path)).Where(File.Exists));
            if (paths.Count == 0)
            {
                if (result.Written.Count > 0)
                {
                    Log.Info("git: nothing to commit");
                }

                return;
            }

            names = string.Join(", ", paths.Select(Named));
            var done = committer.Commit(Committer.Message($"Ingest {names}", $"Filed automatically by doc-filer ({settings})."), paths,
                trailers: git.Trailers, push: git.Push);
            if (done.Error is not null)
            {
                notify("Filed, not committed", [Head(names, 300), see]);
            }
        }
        catch (Exception error) when (error is GitException or InvalidOperationException or Win32Exception or TimeoutException or IOException)
        {
            Log.Error($"git: commit failed: {error.Message}");
            notify("Filed, not committed", [Head(names.Length > 0 ? names : string.Join(", ", result.Written.Select(Named)), 300), see]);
        }
    }

    /// <summary>The notification's line for an archive commit that failed: the subject without its "Archive ".</summary>
    private static string Unarchived(string subject, ArchiveRule rule, int count)
    {
        var text = subject.Length > 0 ? subject : ArchiveSubject(rule, count);
        return text.StartsWith("Archive ", StringComparison.Ordinal) ? text["Archive ".Length..] : text;
    }

    private static string Head(string text, int length) => text.Length > length ? text[..length] : text;
}
