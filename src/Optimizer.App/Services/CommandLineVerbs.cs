using Optimizer.Core.Platform;

namespace Optimizer.App.Services;

/// <summary>Where a switch is explained: README's Command line table, or CONTRIBUTING's switches for the install media.</summary>
internal enum CliHelp { Readme, Contributing }

/// <summary>What a run gets: the switches, the app's services and saved settings, the --log file and the verb's own value.</summary>
internal sealed record CliRun(App.CliArgs Args, AppServices Services, AppSettings Settings, string? Log, string? Value);

/// <summary>A verb: the start runs it without a window, and its exit code ends the process.</summary>
/// <param name="TakesValue">The verb needs a value after it (a file); without one it is no verb, and the window opens.</param>
internal sealed record CliVerb(string Name, bool TakesValue, CliHelp Help, Func<CliRun, Task<int>> Run);

/// <summary>A switch that changes a verb (or, for --uninstall, the window) and is explained with the verbs.</summary>
internal sealed record CliSwitch(string Name, bool TakesValue, CliHelp Help);

/// <summary>
/// The one table of the command line: parsing (<see cref="App.CliArgs.IsCommandLineRun"/>, which also decides in
/// <see cref="Program.Main"/> that a refusal is an exit code, not a dialog), dispatch (<see cref="Find"/>, in App) and the
/// help (README's Command line table and CONTRIBUTING, which a test compares with this table) all use it. The first verb
/// in the list that the start names wins.
/// </summary>
internal static class CommandLineVerbs
{
    public const string Log = "--log";
    public const string Profile = "--profile";
    public const string Include = "--include";
    public const string NoRestorePoint = "--no-restore-point";

    public static IReadOnlyList<CliVerb> Verbs { get; } =
    [
        // The first sign-in on a PC installed from Tunero's media (FirstSignInStart): before the desktop appears, nothing
        // is shown; it installs this exe, starts the installed copy with the profile and ends. A failure is logged and ends
        // the run, so Windows always goes on to the desktop.
        new(Optimizer.Core.Media.FirstSignIn.Switch, false, CliHelp.Contributing, FirstSignInAsync),
        // Developer switch for the VM tests: an install media ISO from a request file (CommandLineMedia).
        new(CommandLineMedia.Switch, true, CliHelp.Contributing,
            r => CommandLineMedia.BuildAsync(r.Value!, r.Log, MediaCommandLineSystem.ForThisPc(r.Services, r.Settings.Theme))),
        new("--apply", true, CliHelp.Readme, r => CommandLineApply.ApplyAsync(r.Value!, r.Services, r.Log, r.Args.Has(NoRestorePoint))),
        new("--apply-recommended", false, CliHelp.Readme,
            r => CommandLineApply.ApplyRecommendedAsync(r.Services, r.Args.Value(Profile), r.Settings.Profile, r.Settings.IgnoredFindings, r.Settings.IgnoredTweaks, r.Log, r.Args.Has(NoRestorePoint))),
        new("--export", true, CliHelp.Readme, r => CommandLineApply.ExportAsync(r.Value!, r.Services, r.Log, r.Args.Value(Include))),
        new(Optimizer.Core.Tweaks.ReapplyTask.Argument, false, CliHelp.Readme, r => CommandLineApply.ReapplyAsync(r.Services, r.Log)),
        new("--undo-all", false, CliHelp.Readme, r => CommandLineApply.UndoAllAsync(r.Services, r.Log)),
    ];

    /// <summary>The switches README explains with the verbs.</summary>
    public static IReadOnlyList<CliSwitch> Switches { get; } =
    [
        new(Profile, true, CliHelp.Readme),
        new(Include, true, CliHelp.Readme),
        new(SingleInstance.Uninstall, false, CliHelp.Readme),
        new(NoRestorePoint, false, CliHelp.Readme),
        new(Log, true, CliHelp.Readme),
    ];

    /// <summary>The verb this start runs, or null for a start with the window.</summary>
    public static CliVerb? Find(App.CliArgs args) => Verbs.FirstOrDefault(v => v.TakesValue ? args.Value(v.Name) is not null : args.Has(v.Name));

    /// <summary>Runs the verb this start names with the app's services and settings.</summary>
    public static Task<int> RunAsync(CliVerb verb, App.CliArgs args, AppServices services, AppSettings settings) =>
        verb.Run(new CliRun(args, services, settings, args.Value(Log), verb.TakesValue ? args.Value(verb.Name) : null));

    private static Task<int> FirstSignInAsync(CliRun r)
    {
        try
        {
            var running = Environment.ProcessPath;
            FirstSignInStart.Run(running, r.Settings, r.Settings.Save,
                InstallService.Steps.ForThisPc(running ?? "", () => StartMenuShortcut.ProgramsFolder(r.Services.Elevation, r.Services.ProfilePath)), new Launch());
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Optimizer.Core.Logging.Log.Error("firstsignin", "the first sign-in run failed", ex);
            return Task.FromResult(CommandLineApply.Error);
        }
    }
}
