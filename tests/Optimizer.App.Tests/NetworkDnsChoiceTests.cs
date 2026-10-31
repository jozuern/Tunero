using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Actions;
using Optimizer.Core.Tweaks;

namespace Optimizer.App.Tests;

/// <summary>
/// The DNS choice on Graphics &amp; DNS (<see cref="NetworkViewModel.SelectedDns"/>): the presets block each other while
/// one has a backup, so choosing another one undoes the preset this app set, waits for the rescan that follows, and
/// applies the new one only when the undo did not already bring it back. One adapter in the registry sandbox, the
/// change runner on the engine with fakes; the page's current-servers line reads nothing from this PC.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class NetworkDnsChoiceTests : IDisposable
{
    private const string Nic = "{AAAA}";
    private const string Interface = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{Nic}";

    private readonly AppFixture _fx = new(elevated: true, Nic);

    public void Dispose() => _fx.Dispose();

    private string NameServer => RegistryValue.Read(_fx.Pc.Registry, Hive.Machine, Interface, "NameServer").Data ?? "";

    private static TweakDefinition Preset(string id) => TweakCatalog.Current.Get($"network.dns.{id}")!;

    /// <summary>The adapter starts with <paramref name="servers"/> ("" for automatic), then this app sets Cloudflare and the page opens.</summary>
    private async Task<NetworkViewModel> PageWithCloudflare(string servers)
    {
        RegistryValue.Write(_fx.Pc.Registry, Hive.Machine, Interface, "NameServer", "string", servers);
        Assert.True(await _fx.Runner.ApplyAsync(Preset("cloudflare")));
        Assert.Equal("1.1.1.1,1.0.0.1", NameServer);
        _fx.Dialogs.ConfirmApplyRequests.Clear();
        var page = new NetworkViewModel(_fx.Host, _fx.Services) { CurrentServers = () => [] };
        await page.EnsureLoadedAsync();
        Assert.Equal(Preset("cloudflare").Id, page.SelectedDns?.Item.Tweak.Id);
        return page;
    }

    /// <summary>Chooses a preset as the combo box does and waits until the page shows the selection again (after the change or its refusal).</summary>
    private static async Task Choose(NetworkViewModel page, string id)
    {
        var notices = 0;
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NetworkViewModel.SelectedDns)) Interlocked.Increment(ref notices);
        };
        page.SelectedDns = page.DnsChoices.Single(c => c.Item.Tweak.Id == Preset(id).Id);
        // One notice from the setter, one when the choice has run.
        await SingleThread.Until(() => Volatile.Read(ref notices) >= 2, "the DNS choice to end");
    }

    [Fact]
    public async Task ChoosingAnotherPresetUndoesTheOldOneFirst()
    {
        var page = await PageWithCloudflare("192.168.1.1");
        // As the window: every change starts a rescan, and the next change waits for it.
        var rescan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fx.Runner.Changed += (_, _) => _fx.Host.ScanInProgress = rescan.Task;

        var choosing = Choose(page, "google");
        await SingleThread.Until(() => _fx.Dialogs.ConfirmApplyCalls == 1 && NameServer == "192.168.1.1", "the undo of Cloudflare");
        Assert.True(_fx.Dialogs.ConfirmApplyRequests[0].IsUndo);
        await Task.Delay(50);
        // Nothing is applied while the rescan runs.
        Assert.Equal(1, _fx.Dialogs.ConfirmApplyCalls);

        rescan.SetResult();
        await choosing.WaitAsync(TimeSpan.FromSeconds(10));
        var apply = _fx.Dialogs.ConfirmApplyRequests[1];
        Assert.False(apply.IsUndo);
        Assert.Equal(_fx.Runner.Title(Preset("google")), apply.Title);
        Assert.Equal("8.8.8.8,8.8.4.4", NameServer);
        Assert.False(_fx.Services.Store.Exists(Preset("cloudflare").Id));
        Assert.True(_fx.Services.Store.Exists(Preset("google").Id));
    }

    [Fact]
    public async Task ACancelledUndoAppliesNothing()
    {
        var page = await PageWithCloudflare("192.168.1.1");
        _fx.Dialogs.ConfirmApplyResult = null;

        await Choose(page, "google");
        Assert.True(Assert.Single(_fx.Dialogs.ConfirmApplyRequests).IsUndo);
        Assert.Equal("1.1.1.1,1.0.0.1", NameServer);
        Assert.True(_fx.Services.Store.Exists(Preset("cloudflare").Id));
        // The combo box shows the servers in use again, not the cancelled choice.
        Assert.Equal(Preset("cloudflare").Id, page.SelectedDns?.Item.Tweak.Id);
    }

    [Fact]
    public async Task AnUndoThatRestoresTheChoiceAppliesNothing()
    {
        // Automatic before Cloudflare: undoing Cloudflare already is the choice.
        var page = await PageWithCloudflare("");

        await Choose(page, "automatic");
        Assert.True(Assert.Single(_fx.Dialogs.ConfirmApplyRequests).IsUndo);
        Assert.Equal("", NameServer);
        Assert.False(_fx.Services.Store.Exists(Preset("automatic").Id));
        Assert.False(_fx.Services.Store.Exists(Preset("cloudflare").Id));
    }
}
