using System.Security.Cryptography;
using System.Text;

namespace DocFiler;

/// <summary>
/// One run at a time per config: watch mode and a run started by hand must never convert
/// or copy the same files at once. A named mutex, keyed on the config's full path, is
/// released by Windows if the process dies, so a crash cannot leave a stale lock behind.
/// </summary>
public sealed class RunLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _held;

    public RunLock(string configPath)
    {
        _mutex = new Mutex(false, $@"Local\doc-filer-{Key(configPath)}");
    }

    /// <summary>A short, stable key for a config file, the same however its path is spelt.</summary>
    public static string Key(string configPath) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath).ToLowerInvariant())))[..16];

    public bool Acquire(TimeSpan wait)
    {
        try
        {
            _held = _mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-run. The lock is ours now; the next run redoes
            // anything it left unfinished, because unchanged files are compared, not trusted.
            _held = true;
        }

        return _held;
    }

    public void Release()
    {
        if (_held)
        {
            _mutex.ReleaseMutex();
            _held = false;
        }
    }

    public void Dispose()
    {
        Release();
        _mutex.Dispose();
    }
}
