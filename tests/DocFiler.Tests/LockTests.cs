using WinBackground;

namespace DocFiler.Tests;

/// <summary>The run lock keeps the mutex name every earlier doc-filer used, so an old and a new build exclude each
/// other during a switch-over.</summary>
public sealed class LockTests
{
    [Theory]
    // Expected values from Python: hashlib.sha256(path.lower().encode()).hexdigest()[:16].upper(), which is the
    // earlier builds' Convert.ToHexString(SHA256(UTF8(Path.GetFullPath(path).ToLowerInvariant())))[..16].
    [InlineData(@"C:\Tools\Doc-Filer.json", "52BD552EBA484469")]
    [InlineData(@"C:\Users\Zoë\Staging\doc-filer.json", "43C2E1A619C1CDD1")]
    public void The_key_and_names_are_unchanged(string path, string key)
    {
        Assert.Equal(key, RunLock.Key(path));
        Assert.Equal($"doc-filer-{key}", RunLock.Name(path));
        Assert.Equal($"doc-filer-watch-{key}", RunLock.WatchName(path));
    }

    [Fact]
    public void The_key_is_the_same_however_the_path_is_spelt()
    {
        var full = Path.Combine(Path.GetTempPath(), "doc-filer-lock-test", "doc-filer.json");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, full);
        Assert.Equal(RunLock.Key(full), RunLock.Key(relative));
        Assert.Equal(RunLock.Key(full), RunLock.Key(full.ToUpperInvariant()));
    }

    /// <summary>An earlier build held a bare .NET Mutex named Local\doc-filer-{key}; the new lock must wait for it,
    /// and the other way round.</summary>
    [Fact]
    public void An_old_builds_mutex_and_the_new_lock_exclude_each_other()
    {
        var config = Path.Combine(Path.GetTempPath(), $"doc-filer-{Guid.NewGuid():N}.json");
        using var taken = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        var old = new Thread(() =>
        {
            using var mutex = new Mutex(false, $@"Local\doc-filer-{RunLock.Key(config)}");
            mutex.WaitOne();
            taken.Set();
            letGo.Wait(TestContext.Current.CancellationToken);
            mutex.ReleaseMutex();
        });
        old.Start();
        taken.Wait(TestContext.Current.CancellationToken);

        using (var runLock = new RunLock(config))
        {
            Assert.False(runLock.Acquire(TimeSpan.Zero));
            letGo.Set();
            old.Join();
            Assert.True(runLock.Acquire(TimeSpan.FromSeconds(5)));

            var blocked = true;
            var probe = new Thread(() =>
            {
                using var mutex = new Mutex(false, $@"Local\doc-filer-{RunLock.Key(config)}");
                blocked = !mutex.WaitOne(0);
                if (!blocked)
                {
                    mutex.ReleaseMutex();
                }
            });
            probe.Start();
            probe.Join();
            Assert.True(blocked);
            runLock.Release();
        }

        Assert.False(Locks.Held(RunLock.Name(config)));
    }

    [Fact]
    public void The_run_lock_waits_and_times_out()
    {
        var config = Path.Combine(Path.GetTempPath(), $"doc-filer-{Guid.NewGuid():N}.json");
        using var first = new RunLock(config);
        using var second = new RunLock(config);
        Assert.True(first.Acquire(TimeSpan.Zero));
        Assert.False(second.Acquire(TimeSpan.FromMilliseconds(200)));
        first.Release();
        Assert.True(second.Acquire(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_one_off_run_waits_for_the_lock_then_gives_up_with_3()
    {
        using var box = new Sandbox();
        var config = FilerConfig.Load(box.ConfigPath);
        using var held = new RunLock(box.ConfigPath);
        Assert.True(held.Acquire(TimeSpan.Zero));
        Assert.Equal(Filer.LockTimedOut, Filer.Run(config, dryRun: true, hook: false, [], TimeSpan.FromMilliseconds(200)));
    }
}
