using Optimizer.Core.Actions;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Tools;

/// <summary>One documented command of a quick fix (a Windows tool in System32, run with a cleaned environment).</summary>
/// <param name="AlwaysRun">Runs even when an earlier step failed (ipconfig /renew after a /release that failed for one adapter).</param>
/// <param name="OkCodes">Exit codes that mean success; null means only 0.</param>
public sealed record QuickFixStep(string File, string Arguments, TimeSpan? Timeout = null, bool AlwaysRun = false, IReadOnlyList<int>? OkCodes = null);

/// <summary>
/// A repair that runs documented Windows commands and changes nothing that needs an undo: it restarts a service or
/// device, renews or resets network state, or rebuilds a Windows cache. Text lives in the app's string resources
/// (Quick_{Id}, Quick_{Id}Hint, Quick_{Id}Action, Quick_{Id}Confirm, Quick_{Id}Done). Every command-only repair of the
/// Tools page is one of these; only Restart Explorer and the icon cache reset have their own code (Explorer must start
/// again as the signed-in user, not elevated).
/// </summary>
public sealed record QuickFix(string Id, bool Confirm, bool NeedsRestart, IReadOnlyList<QuickFixStep> Steps, string Source)
{
    /// <summary>The result text of the tool is shown after a success (chkdsk, reagentc report what they found).</summary>
    public bool ShowOutput { get; init; }

    /// <summary>Can take several minutes: the card and the progress banner say so.</summary>
    public bool TakesMinutes { get; init; }
}

public static class QuickFixes
{
    private static QuickFixStep Ps(string command) => new(PowerShellScript.Exe, PowerShellScript.Command(command));

    public static IReadOnlyList<QuickFix> All { get; } =
    [
        // ipconfig: https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/ipconfig
        new("Dns", Confirm: false, NeedsRestart: false, [new("ipconfig.exe", "/flushdns")],
            "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/ipconfig"),
        // Resets the Winsock catalog to a clean state (removes added layered service providers); needs a restart.
        new("Winsock", Confirm: true, NeedsRestart: true, [new("netsh.exe", "winsock reset")],
            "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-winsock"),
        new("IpRenew", Confirm: true, NeedsRestart: false,
            [new("ipconfig.exe", "/release"), new("ipconfig.exe", "/renew", TimeSpan.FromMinutes(2), AlwaysRun: true)],
            "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/ipconfig"),
        // Overwrites the TCP/IP registry keys, same effect as removing and reinstalling TCP/IP; needs a restart.
        new("TcpIpReset", Confirm: true, NeedsRestart: true,
            [new("netsh.exe", "int ip reset \"" + DataPaths.TcpIpResetLog + "\"")],
            "https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/reset-tcp-ip-net-shell"),
        new("AudioRestart", Confirm: false, NeedsRestart: false, [Ps("Restart-Service -Name Audiosrv -Force")],
            "https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/restart-service"),
        new("BluetoothRestart", Confirm: false, NeedsRestart: false, [Ps("Restart-Service -Name bthserv -Force")],
            "https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/restart-service"),
        new("SearchRestart", Confirm: false, NeedsRestart: false, [Ps("Restart-Service -Name WSearch -Force")],
            "https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/restart-service"),
        // pnputil /restart-device /class needs Windows 11 22H2 or later (the app needs 24H2).
        new("GraphicsRestart", Confirm: true, NeedsRestart: false, [new("pnputil.exe", "/restart-device /class Display")],
            "https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax"),
        new("TimeSync", Confirm: false, NeedsRestart: false, [Ps("Start-Service -Name W32Time"), new("w32tm.exe", "/resync")],
            "https://learn.microsoft.com/en-us/windows-server/networking/windows-time-service/windows-time-service-tools-and-settings"),
        new("PerfCounters", Confirm: false, NeedsRestart: false, [new("lodctr.exe", "/r", TimeSpan.FromMinutes(5))],
            "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/lodctr"),
        // Online scan of the system drive: Windows stays usable, found problems are queued for a fix. It checks the
        // whole volume, which can take several minutes.
        new("DiskScan", Confirm: false, NeedsRestart: false,
            // chkdsk: 0 no errors, 1 errors found and fixed, 2 no cleanup done; 3 means problems remain (its text says which).
            [new("chkdsk.exe", Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\') + " /scan", TimeSpan.FromMinutes(60), OkCodes: [0, 1, 2])],
            "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/chkdsk") { ShowOutput = true, TakesMinutes = true },
        new("RecoveryEnable", Confirm: false, NeedsRestart: false, [new("reagentc.exe", "/enable")],
            "https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/reagentc-command-line-options") { ShowOutput = true },
        // Defender's own signature update from its configured sources (Windows Update by default), as "Check for updates"
        // in Windows Security does.
        new("DefenderUpdate", Confirm: false, NeedsRestart: false, [Ps("Update-MpSignature") with { Timeout = TimeSpan.FromMinutes(10) }],
            "https://learn.microsoft.com/en-us/powershell/module/defender/update-mpsignature"),
        // Restores winget's default sources (msstore, winget, winget-font) and removes added ones: the documented fix when
        // winget cannot open its catalog. Needs administrator rights, so it runs elevated here.
        new("WingetSourceReset", Confirm: true, NeedsRestart: false, [new(WingetTool, "source reset --force --disable-interactivity", TimeSpan.FromMinutes(5))],
            "https://learn.microsoft.com/en-us/windows/package-manager/winget/source") { ShowOutput = true },
        // Restarts into the Advanced startup menu, where Startup Settings offers Safe Mode (for DDU) for this one start.
        // Nothing in the boot configuration changes, so the start after that is normal again: no way back to set up.
        new("AdvancedStartup", Confirm: true, NeedsRestart: true, [new("shutdown.exe", "/r /o /t 0")],
            "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shutdown"),
    ];

    /// <summary>Stands for winget.exe from the protected App Installer folder (<see cref="Apps.Winget.FindTrusted()"/>), resolved when the step runs.</summary>
    public const string WingetTool = "winget";

    public static QuickFix Get(string id) => All.Single(f => f.Id == id);

    /// <summary>
    /// Runs the steps in order. After a failed step (an exit code that is not a success, or a timeout) only the steps
    /// marked <see cref="QuickFixStep.AlwaysRun"/> still run. Output is the text of all steps run.
    /// </summary>
    /// <param name="winget">Finds the trusted winget.exe; null when App Installer is missing. Default: <see cref="Apps.Winget.FindTrusted()"/>.</param>
    public static (bool Ok, string Output) Run(IProcessRunner runner, QuickFix fix, Func<string?>? winget = null)
    {
        var output = new List<string>();
        var ok = true;
        foreach (var step in fix.Steps)
        {
            if (!ok && !step.AlwaysRun) continue;
            var file = step.File == WingetTool ? (winget ?? Apps.Winget.FindTrusted)() : step.File;
            if (file is null)
            {
                output.Add("winget (App Installer) was not found.");
                ok = false;
                continue;
            }
            try
            {
                // winget writes UTF-8 (as Install & update reads its search); Windows' console tools the OEM code page.
                var (code, text) = step.File == WingetTool ? runner.RunUtf8(file, step.Arguments, step.Timeout) : runner.Run(file, step.Arguments, step.Timeout);
                if (text.Trim() is { Length: > 0 } t) output.Add(t);
                if (!(step.OkCodes ?? [0]).Contains(code)) ok = false;
            }
            catch (TimeoutException ex)
            {
                output.Add(ex.Message);
                ok = false;
            }
        }
        return (ok, string.Join("\n", output));
    }
}
