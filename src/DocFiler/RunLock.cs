using System.Security.Cryptography;
using System.Text;
using WinBackground;

namespace DocFiler;

/// <summary>
/// One run at a time per config: watch mode and a run started by hand must never convert
/// or copy the same files at once. A named mutex (WinBackground's NamedLock), keyed on the
/// config's full path, is released by Windows if the process dies, so a crash cannot leave
/// a stale lock behind.
///
/// <para>The mutex is <c>Local\doc-filer-{Key}</c>, exactly as the tool has always named it
/// (16 upper-case hex characters; mutex names are case-sensitive). So a run by this build
/// and one by any earlier build of doc-filer exclude each other, and a switch-over can never
/// convert the same files twice at once. That's why the key is doc-filer's own, not
/// <see cref="Locks.Key"/> (lower-case hex, and the path not made absolute first).</para>
/// </summary>
public sealed class RunLock(string configPath) : IDisposable
{
    private readonly NamedLock _lock = new(Name(configPath));

    /// <summary>A short, stable key for a config file, the same however its path is spelt.</summary>
    public static string Key(string configPath) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath).ToLowerInvariant())))[..16];

    /// <summary>The run lock's name (the mutex is <c>Local\</c> and this).</summary>
    public static string Name(string configPath) => $"doc-filer-{Key(configPath)}";

    /// <summary>The watcher's single-instance lock: one watcher per config.</summary>
    public static string WatchName(string configPath) => $"doc-filer-watch-{Key(configPath)}";

    /// <summary>
    /// Waits up to <paramref name="wait"/>. A lock whose last holder died mid-run counts as
    /// ours: the next run redoes anything it left unfinished, because unchanged files are
    /// compared, not trusted.
    /// </summary>
    public bool Acquire(TimeSpan wait) => _lock.Acquire(wait.TotalSeconds);

    public void Release() => _lock.Release();

    public void Dispose() => _lock.Dispose();
}
