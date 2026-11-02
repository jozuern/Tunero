using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Actions;
using Optimizer.Core.Platform;

namespace Optimizer.App.Tests;

/// <summary>
/// Remove apps &gt; Installed programs without uninstalling anything: the programs come from the uninstall list in the
/// registry sandbox. Uninstall selected is always there and only on with a tick and no queue running (the first tick
/// does not move the list); the line beside it says how to select, then how many are; the count above the list reads
/// as on Startup.
/// </summary>
public sealed class InstalledProgramsListTests : IDisposable
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly AppFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>The page after its read of <paramref name="names"/>, each a program with its own uninstaller in the sandbox of <paramref name="fx"/>.</summary>
    private static async Task<AppsViewModel> Page(AppFixture fx, params string[] names)
    {
        foreach (var name in names)
        {
            RegistryValue.Write(fx.Pc.Registry, Hive.Machine, $@"{Uninstall}\{name}", "DisplayName", "string", name);
            RegistryValue.Write(fx.Pc.Registry, Hive.Machine, $@"{Uninstall}\{name}", "UninstallString", "string", $@"""C:\Program Files\{name}\uninstall.exe""");
        }
        var page = new AppsViewModel(fx.Host, fx.Services, fx.Dialogs);
        await page.LoadProgramsCommand.ExecuteAsync(null);
        return page;
    }

    [Fact]
    public async Task UninstallSelectedWaitsForATickAndNoQueue()
    {
        var page = await Page(_fx, "Contoso Tool", "Discord");
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(page.CanUninstallSelected);
        Assert.Equal(Loc.Instance["Programs_SelectHint"], page.ProgramSelectionText);

        page.ProgramRows[0].IsSelected = true;
        Assert.True(page.CanUninstallSelected);
        Assert.Equal(Loc.Instance.Format("Debloat_SelectedCount", 1), page.ProgramSelectionText);
        Assert.Contains(nameof(AppsViewModel.CanUninstallSelected), changed);
        Assert.Contains(nameof(AppsViewModel.ProgramSelectionText), changed);

        // While a queue runs the button stays where it is, off.
        changed.Clear();
        page.IsQueueRunning = true;
        Assert.False(page.CanUninstallSelected);
        Assert.Contains(nameof(AppsViewModel.CanUninstallSelected), changed);
        page.IsQueueRunning = false;
        Assert.True(page.CanUninstallSelected);

        page.ProgramRows[0].IsSelected = false;
        Assert.False(page.CanUninstallSelected);
        Assert.Equal(Loc.Instance["Programs_SelectHint"], page.ProgramSelectionText);
    }

    [Fact]
    public async Task TheCountReadsAsOnStartup()
    {
        var page = await Page(_fx, "Contoso Tool", "Discord");
        Assert.Equal(Loc.Instance.Format("Programs_Count", 2), page.ProgramsText);
        page.ProgramSearch = "disc";
        Assert.Equal(Loc.Instance.Format("Programs_CountFiltered", 1, 2), page.ProgramsText);
        Assert.False(page.ProgramsNoMatch);
        page.ProgramSearch = "nothing like it";
        Assert.True(page.ProgramsNoMatch);

        using var other = new AppFixture();
        var one = await Page(other, "Contoso Tool");
        Assert.Equal(Loc.Instance["Programs_CountOne"], one.ProgramsText);
    }
}
