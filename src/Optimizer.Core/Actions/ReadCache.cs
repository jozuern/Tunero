using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Optimizer.Core.Actions;

/// <summary>
/// Short-lived results of slow reads (DISM, PowerShell, bcdedit): a scan detects every tweak, then checks drift, and an
/// apply reads the same target several times (preview, state, backup, verify). Each read would start the tool again
/// and take up to seconds. Results are kept for a few seconds for a process runner that enabled the cache (the app's
/// runner; test fakes read every time) and dropped when the action writes. A read asked for while the same read still
/// runs waits for that run instead of starting the tool a second time (the scan starts these reads early, see
/// <see cref="Tweaks.TweakEngine.PrefetchReads"/>). A read that fails with an exception is not kept.
/// </summary>
public static class ReadCache
{
    private sealed class Table
    {
        public required TimeSpan Lifetime { get; init; }
        public ConcurrentDictionary<string, Entry> Values { get; } = new();
    }

    /// <summary>One read: started by the first caller, shared by everyone who asks until it expires.</summary>
    private sealed class Entry
    {
        private readonly Lazy<object?> _value;

        public Entry(Func<object?> read) =>
            _value = new Lazy<object?>(() =>
            {
                var value = read();
                At = DateTime.UtcNow;
                return value;
            }, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>When the read finished (its age counts from then).</summary>
        public DateTime At { get; private set; }

        public bool Done => _value.IsValueCreated;
        public object? Value => _value.Value;
    }

    private static readonly ConditionalWeakTable<IProcessRunner, Table> Tables = new();

    /// <summary>Caches slow reads made through this runner for <paramref name="lifetime"/>.</summary>
    public static void Enable(IProcessRunner runner, TimeSpan lifetime) => Tables.AddOrUpdate(runner, new Table { Lifetime = lifetime });

    public static bool IsEnabled(IProcessRunner runner) => Tables.TryGetValue(runner, out _);

    public static StoredValue? Get(ActionContext c, string key, Func<StoredValue?> read) => Get(c.Processes, key, read);

    public static T Get<T>(IProcessRunner runner, string key, Func<T> read)
    {
        if (!Tables.TryGetValue(runner, out var table)) return read();
        while (true)
        {
            var entry = table.Values.GetOrAdd(key, _ => new Entry(() => read()));
            if (entry.Done && DateTime.UtcNow - entry.At >= table.Lifetime)
            {
                table.Values.TryRemove(KeyValuePair.Create(key, entry));
                continue;
            }
            try
            {
                return (T)entry.Value!;
            }
            catch (Exception)
            {
                // A failed read is not kept: the next caller tries again (Lazy would rethrow the same exception).
                table.Values.TryRemove(KeyValuePair.Create(key, entry));
                throw;
            }
        }
    }

    /// <summary>
    /// Runs a write of the value under <paramref name="key"/>: the kept value is dropped before and after it (also when
    /// it fails), so a read that ran while the tool was writing cannot keep the old value.
    /// </summary>
    public static void Writing(IProcessRunner runner, string key, Action write)
    {
        Invalidate(runner, key);
        try
        {
            write();
        }
        finally
        {
            Invalidate(runner, key);
        }
    }

    public static void Writing(ActionContext c, string key, Action write) => Writing(c.Processes, key, write);

    public static void Invalidate(IProcessRunner runner, string key)
    {
        if (Tables.TryGetValue(runner, out var table)) table.Values.TryRemove(key, out _);
    }
}
