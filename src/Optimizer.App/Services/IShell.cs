using Optimizer.Core.Platform;
using Optimizer.Core.Updates;
using Wpf.Ui.Controls;

namespace Optimizer.App.Services;

/// <summary>
/// What services and pages may ask of the window, so they do not need the window or its view model: a result line, a
/// link in the signed-in user's browser, and closing the app (after an update or uninstall took over).
/// </summary>
public interface IShell
{
    void ShowResult(string text, InfoBarSeverity severity = InfoBarSeverity.Informational);

    void OpenLink(string url);

    void Shutdown();
}

/// <summary>Where this copy runs and how it can be replaced: what the update and the uninstall decide by.</summary>
public sealed class InstallFacts(string? exePath, bool isPublishedExe, bool elevated, Func<PackageInstall?> package, ElevationInfo? elevation)
{
    private readonly Lazy<PackageInstall?> _package = new(package);

    public string? ExePath { get; } = exePath;

    /// <summary>The published single-file exe (a Debug or Release build has Tunero.dll next to its exe).</summary>
    public bool IsPublishedExe { get; } = isPublishedExe;

    public bool Elevated { get; } = elevated;

    /// <summary>Installed with winget, Chocolatey or Scoop; null for a downloaded copy. Read once, when first needed.</summary>
    public PackageInstall? Package => _package.Value;

    public ElevationInfo? Elevation { get; } = elevation;

    public static bool PublishedExe(string? exe) => exe is not null &&
        System.IO.Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        !System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe) ?? "", typeof(InstallFacts).Assembly.GetName().Name + ".dll"));

    public static InstallFacts ForThisProcess(AppServices services)
    {
        var exe = Environment.ProcessPath;
        var published = PublishedExe(exe);
        return new InstallFacts(exe, published, DataPaths.ProcessIsElevated,
            () => exe is not null && published ? PackageInstall.Detect(exe, services.ProfilePath) : null, services.Elevation);
    }
}

/// <summary>How the update and the uninstall start programs; tests replace both so nothing starts.</summary>
public sealed class Launch
{
    /// <summary>Starts a hardened program (<see cref="ProcessLauncher"/>) and returns its process id.</summary>
    public Func<System.Diagnostics.ProcessStartInfo, int> Start { get; init; } = psi =>
    {
        using var p = ProcessLauncher.Start(psi);
        return p.Id;
    };

    /// <summary>Opens a program as the signed-in user (<see cref="DeElevatedLauncher"/>); false when that failed.</summary>
    public Func<string, string?, ElevationInfo?, bool> OpenAsUser { get; init; } =
        (file, arguments, elevation) => DeElevatedLauncher.Open(file, arguments, elevation) != DeElevatedLauncher.Path.Failed;

    /// <summary>
    /// Runs a program in this process's rights with its output read line by line (<see cref="StreamingProcess"/>), in the
    /// given encoding (null: UTF-8) and with the hardened environment changed as the next argument says; returns the exit
    /// code. Cancelling ends the program and what it started.
    /// </summary>
    public Func<string, string, IProgress<string>?, System.Text.Encoding?, Action<IDictionary<string, string?>>?, CancellationToken, Task<int>> RunStreaming { get; init; } =
        (file, arguments, lines, encoding, environment, ct) => StreamingProcess.RunAsync(file, arguments, lines, ct, encoding, environment: environment);
}
