using System.IO;
using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Actions;
using Optimizer.Core.Services;
using Optimizer.Core.Startup;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// A service's start type on Services &amp; tasks (<see cref="ServicesViewModel.ChangeStartAsync"/> and the combo box of
/// <see cref="ServiceRowVm"/>): back to the original start type undoes the change instead of making a second one, a
/// backup that cannot be read changes nothing, and a cancelled or refused change shows the start type in effect. The
/// service is a fake one of a vendor (no catalog note, a vendor signature), the start types live in the fake service
/// manager, and the engine applies and undoes through the change runner.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class ServiceStartTests : IDisposable
{
    private const string Name = "TuneroTestVendorUpdater";
    private static readonly SignatureInfo Vendor = new(SignatureStatus.Signed, "Vendor Inc.", Catalog: false, MicrosoftRoot: false);

    private readonly AppFixture _fx = new();

    public ServiceStartTests() => _fx.Pc.Services.Start[Name] = ServiceStart.Automatic;

    public void Dispose() => _fx.Dispose();

    /// <summary>The service as the service list reads it now.</summary>
    private ServiceRow Read() =>
        new(Name, "Vendor Updater", null, _fx.Pc.Services.GetStartType(Name), true, @"""C:\Program Files\Vendor\updater.exe""", @"C:\Program Files\Vendor\updater.exe", null)
        {
            Signature = Vendor,
        };

    private async Task<ServicesViewModel> Page()
    {
        var page = new ServicesViewModel(_fx.Host, _fx.Services, _fx.Runner) { ReadServices = () => [Read()] };
        await page.EnsureLoadedAsync();
        return page;
    }

    private static ServiceRowVm Row(ServicesViewModel page) => page.Rows.Single(r => r.Name == Name);

    private string Id => ServiceManager.ChangeId(Name);

    /// <summary>Chooses a start type in the row's combo box and waits until the row shows the start type in effect again.</summary>
    private static async Task ChooseRefused(ServiceRowVm row, ServiceStart start)
    {
        var notices = 0;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServiceRowVm.Selected)) Interlocked.Increment(ref notices);
        };
        row.Selected = row.Options.Single(o => o.Start == start);
        // One notice from the choice, one when the row goes back.
        await SingleThread.Until(() => Volatile.Read(ref notices) >= 2, "the combo box to show the start type in effect");
    }

    [Fact]
    public async Task BackToTheOriginalStartTypeUndoesTheChange()
    {
        var page = await Page();
        Assert.True(await page.ChangeStartAsync(Row(page), ServiceStart.Manual));
        Assert.Equal(ServiceStart.Manual, _fx.Pc.Services.Start[Name]);
        Assert.Equal("Automatic", _fx.Services.Store.Get(Id)!.Entries.Single().Original.Data);
        Assert.Equal(ServiceStart.Manual, Row(page).Selected?.Start);

        Assert.True(await page.ChangeStartAsync(Row(page), ServiceStart.Automatic));
        Assert.True(_fx.Dialogs.ConfirmApplyRequests[^1].IsUndo);
        Assert.Equal(ServiceStart.Automatic, _fx.Pc.Services.Start[Name]);
        // No change is left that only sets what was there before.
        Assert.False(_fx.Services.Store.Exists(Id));
    }

    [Fact]
    public async Task AnUnreadableBackupChangesNothing()
    {
        var page = await Page();
        Assert.True(await page.ChangeStartAsync(Row(page), ServiceStart.Manual));
        var confirmations = _fx.Dialogs.ConfirmApplyCalls;
        var backup = Directory.GetFiles(_fx.Services.Store.BackupFolder, "*.json").Single(f => Path.GetFileName(f).StartsWith(Id, StringComparison.OrdinalIgnoreCase));

        // Another program (a virus scanner) holds the backup file: without its original a change could record Manual as one.
        using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
            await ChooseRefused(Row(page), ServiceStart.Disabled);

        Assert.Equal(ServiceStart.Manual, _fx.Pc.Services.Start[Name]);
        Assert.Equal(ServiceStart.Manual, Row(page).Selected?.Start);
        Assert.Equal(confirmations, _fx.Dialogs.ConfirmApplyCalls);
        Assert.Contains(_fx.Host.Results, r => r.Severity == InfoBarSeverity.Error && r.Text.Contains(backup, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Automatic", _fx.Services.Store.Get(Id)!.Entries.Single().Original.Data);
    }

    [Fact]
    public async Task ACancelledChangeShowsTheOldStartType()
    {
        var page = await Page();
        _fx.Dialogs.ConfirmApplyResult = null;

        await ChooseRefused(Row(page), ServiceStart.Disabled);
        Assert.Equal(1, _fx.Dialogs.ConfirmApplyCalls);
        Assert.Equal(ServiceStart.Automatic, Row(page).Selected?.Start);
        Assert.Equal(ServiceStart.Automatic, _fx.Pc.Services.Start[Name]);
        Assert.False(_fx.Services.Store.Exists(Id));
    }
}
