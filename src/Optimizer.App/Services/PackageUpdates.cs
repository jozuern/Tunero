using Optimizer.Core.Actions;
using Optimizer.Core.Apps;
using Optimizer.Core.Logging;
using Optimizer.Core.Platform;
using Optimizer.Core.Updates;

namespace Optimizer.App.Services;

/// <summary>
/// "Update all apps" over every package manager it may run, and Chocolatey's update list. Elevated runs (winget for the
/// whole PC, Chocolatey from its checked folder with its hardened environment) stream their output; Scoop, winget's
/// per-user packages and the Store open as the signed-in user, never elevated. Tests replace <see cref="Launch"/>.
/// </summary>
public sealed class PackageUpdates(Launch launch, ElevationInfo? elevation)
{
    /// <summary>How long choco outdated may take before it is ended (it waits for every package source it cannot reach).</summary>
    public TimeSpan OutdatedTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The question before Update all: which managers run, in order, and what each one does.</summary>
    public static string ConfirmText(IReadOnlyList<PackageManagerInfo> plan)
    {
        var l = Loc.Instance;
        // Several package managers, each downloading and installing: "This can take several minutes" belongs to the run.
        var parts = new List<string> { Durations.WithSentence(l.Format("Apps_UpdateAllRuns", string.Join(", ", plan.Select(m => m.Name)))) };
        foreach (var m in plan)
            parts.Add(m.Kind switch
            {
                // winget for the whole PC and the user's window, the whole PC alone (no winget for the user), or the window alone.
                ManagerKind.Winget when m.Run == ManagerRun.AsUser => l["Apps_UpdateAllWingetUser"],
                ManagerKind.Winget when m.UserPath is null => l["Apps_UpdateAllWingetMachine"],
                ManagerKind.Winget => l["Apps_UpdateAllWinget"],
                ManagerKind.Chocolatey => l["Apps_UpdateAllChoco"],
                ManagerKind.Scoop => l["Apps_UpdateAllScoop"],
                _ => l["Apps_UpdateAllStore"],
            });
        parts.Add(l["Apps_UpdateAllRestart"]);
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Runs the plan one manager after the other and returns one result line per run (two for winget: the whole PC, then
    /// the user's window). <paramref name="lines"/> gets the output of the elevated runs. Nothing outside the plan's
    /// runs starts: an untrusted Chocolatey and the developer tools are skipped here too.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAllAsync(IReadOnlyList<PackageManagerInfo> plan, IProgress<string> lines)
    {
        var l = Loc.Instance;
        var results = new List<string>();
        foreach (var m in plan.Where(m => m.InUpdateAll && !m.IsDeveloperTool))
        {
            switch (m.Kind)
            {
                case ManagerKind.Winget:
                    if (m.Run == ManagerRun.Elevated)
                    {
                        var code = await launch.Processes.RunAsync(m.Path, Winget.UpgradeAllArguments("machine"), null, CancellationToken.None, lines);
                        results.Add(Winget.IsSuccess(code) ? l["Apps_UpdateAllDone"] : l.Format("Apps_UpdateFailed", $"0x{code:X8}"));
                    }
                    // Per-user programs update as the signed-in user, in a window of their own.
                    var user = m.Run == ManagerRun.AsUser ? m.Path : m.UserPath;
                    if (user is not null && launch.OpenAsUser(user, Winget.UpgradeAllArguments("user"), elevation))
                        results.Add(l["Apps_UpdateAllUser"]);
                    break;
                case ManagerKind.Chocolatey when m.Run == ManagerRun.Elevated && m.Root is { } root:
                    var choco = await launch.Processes.RunAsync(m.Path, Chocolatey.UpgradeAllArguments, null, CancellationToken.None, lines, ChocolateyRun(root));
                    Log.Info("apps", "chocolatey upgrade all", new { exitCode = choco });
                    results.Add(Chocolatey.IsSuccess(choco) ? l["Apps_ChocoDone"] : l.Format("Apps_ChocoUpdateFailed", choco));
                    break;
                case ManagerKind.Scoop when m.Run == ManagerRun.AsUser:
                    var scoop = launch.OpenAsUser(WingetPackage.PowerShell, PackageManagers.ScoopUpdateArguments(m.Path, l["Apps_ScoopWindowDone"]), elevation);
                    results.Add(scoop ? l["Apps_ScoopStarted"] : l["Apps_ScoopNotStarted"]);
                    break;
                case ManagerKind.Store:
                    results.Add(launch.OpenAsUser(PackageManagers.StoreUpdatesLink, null, elevation) ? l["Apps_StoreOpened"] : l["Apps_StoreNotOpened"]);
                    break;
            }
        }
        return results;
    }

    /// <summary>
    /// choco outdated from the checked folder: the packages with a newer version, or null when Chocolatey failed or could
    /// not reach its source (<see cref="Chocolatey.OutdatedResult"/>). Contacts the package sources; started by the user only. After
    /// <see cref="OutdatedTimeout"/> Chocolatey is ended and a <see cref="TimeoutException"/> follows (a read: ending it
    /// changes nothing).
    /// </summary>
    public async Task<IReadOnlyList<ChocolateyOutdated>?> ChocolateyOutdatedAsync(PackageManagerInfo choco)
    {
        var root = TrustedRoot(choco);
        var output = new Collected();
        var code = await launch.Processes.RunAsync(choco.Path, Chocolatey.OutdatedArguments, OutdatedTimeout, CancellationToken.None, output, ChocolateyRun(root));
        return Chocolatey.OutdatedResult(code, output.Text);
    }

    /// <summary>choco upgrade for one package of the update list; returns Chocolatey's exit code.</summary>
    public Task<int> ChocolateyUpgradeAsync(PackageManagerInfo choco, string id, IProgress<string> lines)
    {
        var arguments = Chocolatey.UpgradeArguments(id);
        var root = TrustedRoot(choco);
        // No time limit: ending an installer in the middle can leave a program half updated.
        return launch.Processes.RunAsync(choco.Path, arguments, null, CancellationToken.None, lines, ChocolateyRun(root));
    }

    /// <summary>Chocolatey writes the OEM code page and runs with its root set to the checked folder.</summary>
    private static RunOptions ChocolateyRun(string root) => RunOptions.Oem with { Environment = env => Chocolatey.HardenEnvironment(env, root) };

    private static string TrustedRoot(PackageManagerInfo choco) =>
        choco is { Kind: ManagerKind.Chocolatey, Run: ManagerRun.Elevated, Root: { } root } ? root
        : throw new InvalidOperationException("Chocolatey runs only from a folder that only administrators can change");

    /// <summary>Collects on the reading thread: the parse after the run must see every line.</summary>
    private sealed class Collected : IProgress<string>
    {
        private readonly System.Text.StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text) return _text.ToString();
            }
        }

        public void Report(string value)
        {
            lock (_text) _text.AppendLine(value);
        }
    }
}
