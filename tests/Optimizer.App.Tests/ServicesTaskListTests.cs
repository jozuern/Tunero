using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Actions;
using Optimizer.Core.Startup;

namespace Optimizer.App.Tests;

/// <summary>
/// The Scheduled tasks tab of Services &amp; tasks: with Show Windows tasks off a task is hidden only when everything it
/// runs is Windows' own, as the Startup page lists each program of a task on its own. The tasks are fakes and the
/// signatures come from a fake verifier (a file under C:\Windows is Microsoft's); nothing is read from this PC.
/// </summary>
public sealed class ServicesTaskListTests : IDisposable
{
    private static readonly SignatureInfo Microsoft = new(SignatureStatus.Signed, "Microsoft Windows", Catalog: true, MicrosoftRoot: true);
    private static readonly SignatureInfo Unsigned = new(SignatureStatus.Unsigned, null, Catalog: false);

    private const string Taskkill = @"C:\Windows\System32\taskkill.exe";
    private const string Planted = @"C:\Users\Public\x.exe";

    private static SignatureInfo Verify(string? file) => file?.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) == true ? Microsoft : Unsigned;

    private readonly AppFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task ASecondProgramCannotHideBehindASignedFirstOne()
    {
        _fx.Pc.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Updater", true, "Vendor", Taskkill, "/im foo.exe", false, false, null, [(Planted, null)]));
        _fx.Pc.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Cleanup", true, "Vendor", Taskkill, "/im bar.exe", false, false, null));

        var page = new ServicesViewModel(_fx.Host, _fx.Services, _fx.Runner) { ReadServices = () => [], VerifySignature = Verify };
        await page.EnsureLoadedAsync();

        Assert.False(page.ShowMicrosoftTasks);
        // Only Windows' own program: hidden. A second program of its own: listed, with what needs a look.
        var row = Assert.Single(page.Tasks);
        Assert.Equal("Updater", row.Name);
        Assert.Equal((Planted, $"\"{Planted}\""), (row.Entry.ImagePath, row.Command));
        Assert.False(row.IsPlainMicrosoft);
        Assert.True(row.NeedsAttention);
        Assert.Equal([Loc.Instance["Startup_TagUnsigned"]], row.AttentionTags);

        page.ShowMicrosoftTasks = true;
        Assert.Equal(["Cleanup", "Updater"], page.Tasks.Select(t => t.Name));
        // The search finds a task by its other programs too.
        page.TaskSearch = "x.exe";
        Assert.Equal(["Updater"], page.Tasks.Select(t => t.Name));
    }

    /// <summary>The first action that needs a look decides; when none does, the first one (its file and its command).</summary>
    [Fact]
    public void TheActionThatNeedsALookDecides()
    {
        var task = new ScheduledTaskInfo(@"\Vendor\Two", true, "Vendor", null, null, true, false, null);
        static StartupScanner.TaskAction Action(string file, string suffix) => new($"\"{file}\"", file, suffix);

        var check = ServicesViewModel.CheckTask(task, [Action(Taskkill, ""), Action(Planted, "#2")], Verify);
        Assert.Equal((Planted, SignatureStatus.Unsigned), (check.Image, check.Signature.Status));
        check = ServicesViewModel.CheckTask(task, [Action(Taskkill, ""), Action(@"C:\Windows\System32\notepad.exe", "#2")], Verify);
        Assert.Equal((Taskkill, $"\"{Taskkill}\""), (check.Image, check.Command));
        // A script host is never Windows' own, whoever signed it.
        const string ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        check = ServicesViewModel.CheckTask(task, [Action(Taskkill, ""), new StartupScanner.TaskAction($"{ps} -enc SQBFAFgA", ps, "#2")], Verify);
        Assert.Equal((ps, true), (check.Image, check.RunsScriptHost));
        // A COM handler the user's own registration replaces needs a look, even with a DLL Microsoft signed.
        const string dll = @"C:\Windows\System32\shell32.dll";
        check = ServicesViewModel.CheckTask(task, [Action(Taskkill, ""), new StartupScanner.TaskAction(dll, dll, "#com", UserOwnClass: true)], Verify);
        Assert.Equal((dll, true), (check.Image, check.UserOwnClass));
    }

    /// <summary>
    /// One of Windows' own tasks whose COM handler the signed-in user's own registration replaces (no administrator rights
    /// needed): listed with Show Windows tasks off, marked Unusual, with the user's DLL. The same task on the machine's
    /// class stays hidden as Windows' own.
    /// </summary>
    [Fact]
    public async Task AWindowsTaskWhoseHandlerTheUserReplacedIsListed()
    {
        const string replaced = "{0358B920-0AC7-461F-98F4-58E32CD89148}", plain = "{0358B920-0AC7-461F-98F4-58E32CD8914A}";
        foreach (var id in new[] { replaced, plain })
            RegistryValue.Write(_fx.Pc.Registry, Hive.Machine, $@"SOFTWARE\Classes\CLSID\{id}\InprocServer32", "", "string", @"C:\Windows\System32\wininet.dll");
        RegistryValue.Write(_fx.Pc.Registry, Hive.User, $@"Software\Classes\CLSID\{replaced}\InprocServer32", "", "string", Planted);
        _fx.Pc.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Microsoft\Windows\Wininet\CacheTask", true, "Microsoft", null, null, true, false, null, ComHandler: replaced));
        _fx.Pc.Tasks.Listed.Add(new ScheduledTaskInfo(@"\Microsoft\Windows\Wininet\Other", true, "Microsoft", null, null, true, false, null, ComHandler: plain));

        var page = new ServicesViewModel(_fx.Host, _fx.Services, _fx.Runner) { ReadServices = () => [], VerifySignature = Verify };
        await page.EnsureLoadedAsync();

        var row = Assert.Single(page.Tasks);
        Assert.Equal((@"Microsoft\Windows\Wininet\CacheTask", Planted), (row.Name, row.Entry.ImagePath));
        Assert.True(row.NeedsAttention);
        Assert.Contains(Loc.Instance["Startup_TagUnusual"], row.AttentionTags);
    }
}
