using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Catalog;
using Optimizer.Core.Debloat;
using Optimizer.Core.Platform;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// Remove apps (<see cref="DebloatViewModel"/>): what is removed is exactly what was ticked and confirmed, an app that
/// fails does not stop the others and is named, the confirmation names the apps the Store may not give back, nothing is
/// removed without administrator rights, and an app that came back is named until dismissed. The installed packages are
/// a fake Get-AppxPackage behind the fake process runner; the removed-apps log is in the tests' data folder; OneDrive and
/// Edge are given, not read.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed partial class DebloatViewModelTests : IDisposable
{
    private static readonly AppxCatalog Appx = CatalogData.Current.Appx;

    /// <summary>Reviewed apps without a guard, picked from the catalog at test time: two the Store gives back, one it may not.</summary>
    private static readonly AppxEntry[] Plain = Appx.Apps.Where(e => e.Guard is null && !e.Suffix && !Appx.IsProtected(e.Name)).ToArray();

    private static readonly AppxEntry StoreA = Plain.First(e => e.CanReinstall);
    private static readonly AppxEntry StoreB = Plain.Where(e => e.CanReinstall).Skip(1).First();
    private static readonly AppxEntry NoStore = Plain.First(e => !e.CanReinstall);

    private static string RemovedLog => Path.Combine(DataPaths.Root, "removed-apps.json");

    private AppFixture? _fx;
    private AppFixture Fx => _fx ?? throw new InvalidOperationException("no page yet");

    /// <summary>The packages Get-AppxPackage lists, the ones whose removal fails, and what the page asked for.</summary>
    private readonly List<string> _installed = [];
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _removals = [];
    private readonly List<string> _listings = [];

    public DebloatViewModelTests() => DeleteLog();

    public void Dispose()
    {
        _fx?.Dispose();
        DeleteLog();
    }

    /// <summary>The data folder is shared by the tests of this process: each starts without removed apps.</summary>
    private static void DeleteLog()
    {
        if (File.Exists(RemovedLog)) File.Delete(RemovedLog);
    }

    private (int, string)? Respond(string file, string arguments)
    {
        if (file != PowerShellScript.Exe) return null;
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[(arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16)..]));
        lock (_installed)
        {
            if (script.Contains("Remove-AppxPackage", StringComparison.Ordinal))
            {
                var name = RemovedName().Match(script).Groups[1].Value;
                _removals.Add(name);
                if (_failing.Contains(name)) return (1, "Deployment failed with HRESULT: 0x80073CFA");
                _installed.Remove(name);
                return (0, "");
            }
            _listings.Add(script);
            return (0, JsonSerializer.Serialize(_installed.Select(n => new { Name = n, PackageFamilyName = n + "_8wekyb3d8bbwe", Version = "1.0.0.0", NonRemovable = false })));
        }
    }

    [GeneratedRegex("-Name '([^']+)'")]
    private static partial Regex RemovedName();

    private async Task<DebloatViewModel> Page(bool elevated, params AppxEntry[] installed)
    {
        _fx = new AppFixture(elevated);
        _fx.Pc.Processes.Handler = Respond;
        _installed.AddRange(installed.Select(e => e.Name));
        var page = new DebloatViewModel(_fx.Host, _fx.Services, _fx.Dialogs)
        {
            ReadOneDrive = () => new OneDriveState(null, null, [], 0, false),
            ReadEdge = () => new EdgeState(false, false, null, null),
        };
        await page.EnsureLoadedAsync();
        return page;
    }

    private static Optimizer.App.ViewModels.DebloatItem Item(DebloatViewModel page, AppxEntry entry) => page.Items.Single(i => i.PackageName == entry.Name);

    private static string Label(AppxEntry entry) => entry.Label(Loc.Instance.Language);

    [Fact]
    public async Task RemovingTheTickedAppsGoesOnAfterAFailureAndNamesIt()
    {
        var page = await Page(elevated: true, StoreA, StoreB, NoStore);
        _failing.Add(StoreB.Name);
        Item(page, StoreA).IsSelected = true;
        Item(page, StoreB).IsSelected = true;
        Assert.Equal(2, page.SelectedCount);

        await page.RemoveSelectedCommand.ExecuteAsync(null);

        // Only the ticked apps, each once; the one left unticked is not touched.
        Assert.Equal(new[] { StoreA.Name, StoreB.Name }.Order(StringComparer.Ordinal), _removals.Order(StringComparer.Ordinal));
        Assert.Contains((Loc.Instance.Format("Debloat_RemovedSome", 1, Label(StoreB)), InfoBarSeverity.Warning), Fx.Host.Results);
        // After the reload: the removed app is gone and listed as removed with its Store link, the one that failed stays ticked.
        Assert.DoesNotContain(page.Items, i => i.PackageName == StoreA.Name);
        Assert.True(Item(page, StoreB).IsSelected);
        Assert.False(Item(page, NoStore).IsSelected);
        Assert.Equal(1, page.SelectedCount);
        var removed = Assert.Single(page.Removed);
        Assert.Equal(Label(StoreA), removed.Name);
        Assert.NotNull(removed.StoreLink);
    }

    [Fact]
    public async Task TheConfirmationNamesTheAppsTheStoreMayNotGiveBack()
    {
        var page = await Page(elevated: true, StoreA, NoStore);
        Item(page, StoreA).IsSelected = true;
        Item(page, NoStore).IsSelected = true;
        Fx.Dialogs.Answer = false;

        await page.RemoveSelectedCommand.ExecuteAsync(null);

        var text = Assert.Single(Fx.Dialogs.AskedTexts);
        Assert.Contains(Loc.Instance.Format("Debloat_ConfirmSelectedNoStore", Label(NoStore)), text);
        // Cancelled: nothing is removed and the ticks stay.
        Assert.Empty(_removals);
        Assert.Equal(2, page.SelectedCount);
    }

    [Fact]
    public async Task WithoutAdministratorRightsNothingIsRemoved()
    {
        var page = await Page(elevated: false, StoreA);
        // Listed for this account only, and offered without a way to remove it.
        Assert.DoesNotContain("-AllUsers", Assert.Single(_listings));
        var item = Item(page, StoreA);
        Assert.False(item.CanRemove);

        item.IsSelected = true;
        await page.RemoveSelectedCommand.ExecuteAsync(null);
        await page.RemoveCommand.ExecuteAsync(item);
        Assert.Empty(Fx.Dialogs.Asked);
        Assert.Empty(_removals);
    }

    [Fact]
    public async Task AnAppThatCameBackIsNamedUntilDismissed()
    {
        var page = await Page(elevated: true, StoreA);
        await page.RemoveCommand.ExecuteAsync(Item(page, StoreA));
        Assert.Contains((Loc.Instance.Format("Debloat_Removed", Label(StoreA)), InfoBarSeverity.Success), Fx.Host.Results);
        Assert.Null(page.CameBackText);

        // A feature update installs it again.
        lock (_installed) _installed.Add(StoreA.Name);
        await page.ReloadAsync();
        Assert.Contains(StoreA.Name, page.CameBackText);

        await page.DismissCameBackCommand.ExecuteAsync(null);
        Assert.Null(page.CameBackText);
        Assert.Empty(page.Removed);
        await page.ReloadAsync();
        Assert.Null(page.CameBackText);
    }
}
