using System.Runtime.CompilerServices;
using Optimizer.Core.Logging;

namespace Optimizer.Core.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Tests keep the log in memory, and the data folder points at a temp folder: nothing may be written into the real
    /// data folder of this PC (its backups, the change lock, the crash report).
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        Optimizer.Core.Platform.DataPaths.UseRoot(Path.Combine(Path.GetTempPath(), "tunero-tests-" + Environment.ProcessId));
        Log.DisableFile();
        // The Recycle Bin is the real user's: a test that reaches the shell call has lost a refusal it relied on. It fails
        // here, before anything is moved; every check before the call stays real.
        Optimizer.Core.Tools.StorageAnalyzer.ShellRecycle = path =>
        {
            lock (RecycleBinCalls) RecycleBinCalls.Add(path);
            throw new RecycleBinReachedException(path);
        };
    }

    /// <summary>Paths a test handed to the Recycle Bin's shell call (each one also failed that test).</summary>
    internal static readonly List<string> RecycleBinCalls = [];
}

/// <summary>Thrown instead of moving anything when a test reaches the Recycle Bin.</summary>
internal sealed class RecycleBinReachedException(string path)
    : Exception($"a test reached the Recycle Bin with {path}: nothing was moved, but a refusal the test relies on is gone");

/// <summary>
/// Test classes that change a process-wide setting for a moment (ProcessHardening.TempFolder, StreamingProcess.OutputGrace,
/// an environment variable) run alone, after the others: a parallel test would otherwise see the changed value.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalStateCollection
{
    public const string Name = "Global state";
}
