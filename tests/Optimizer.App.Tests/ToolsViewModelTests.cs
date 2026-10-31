using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Actions;
using Optimizer.Core.Startup;
using Optimizer.Core.Tools;
using Wpf.Ui.Controls;

namespace Optimizer.App.Tests;

/// <summary>
/// The Tools page (<see cref="ToolsViewModel"/>): start priority rules go through the engine (confirmation, backup) into
/// Image File Execution Options in the registry sandbox, Remove undoes a rule of this app and backs up a rule of another
/// program before removing it, and a quick fix runs as the only change. DISM and the quick fix's tools are the fake
/// process runner; nothing starts.
/// </summary>
[Collection(nameof(ChangeGateCollection))]
public sealed class ToolsViewModelTests : IDisposable
{
    private readonly AppFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static string PerfOptions(string exe) => $@"{StartupScanner.Ifeo}\{exe}\PerfOptions";

    private StoredValue Cpu(string exe) => RegistryValue.Read(_fx.Pc.Registry, Hive.Machine, PerfOptions(exe), "CpuPriorityClass");

    private async Task<ToolsViewModel> Page()
    {
        var page = new ToolsViewModel(_fx.Host, _fx.Services, _fx.Runner, _fx.Dialogs);
        await page.EnsureLoadedAsync();
        return page;
    }

    private static void Pick(ToolsViewModel page, CpuPriority cpu) => page.NewPriority = page.PriorityChoices.Single(c => c.Key == cpu.ToString());

    [Fact]
    public async Task AProgramPathIsNoRuleName()
    {
        var page = await Page();
        page.NewPriorityExe = @"C:\Games\game.exe";
        await page.AddPriorityRuleCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Instance["Prio_InvalidName"], page.PriorityNote);
        Assert.Equal(0, _fx.Dialogs.ConfirmApplyCalls);
        Assert.Empty(ProgramPriority.Read(_fx.Pc.Registry));
    }

    [Fact]
    public async Task ARuleIsAddedListedAndRemovedByItsUndo()
    {
        var page = await Page();
        page.NewPriorityExe = "game.exe";
        Pick(page, CpuPriority.High);
        await page.AddPriorityRuleCommand.ExecuteAsync(null);

        Assert.Equal(((int)CpuPriority.High).ToString(System.Globalization.CultureInfo.InvariantCulture), Cpu("game.exe").Data);
        var row = Assert.Single(page.PriorityRules);
        Assert.Equal(("game.exe", Loc.Instance["Prio_High"]), (row.Exe, row.CpuText));
        Assert.Equal("", page.NewPriorityExe);
        var ours = ProgramPriority.Tweak("game.exe", CpuPriority.High, lowIo: false);
        Assert.True(_fx.Services.Store.Exists(ours.Id));

        // Remove on a rule of this app is its undo: the key is as before, and no second change is left on Changes.
        await page.RemovePriorityRuleCommand.ExecuteAsync(row);
        Assert.True(_fx.Dialogs.ConfirmApplyRequests[^1].IsUndo);
        Assert.False(Cpu("game.exe").Existed);
        Assert.Empty(page.PriorityRules);
        Assert.False(_fx.Services.Store.Exists(ours.Id));
        Assert.False(_fx.Services.Store.Exists(ProgramPriority.RemoveTweak("game.exe").Id));
    }

    [Fact]
    public async Task RemovingAnotherProgramsRuleBacksItUp()
    {
        // Set by another tool: Realtime, which this app does not offer.
        RegistryValue.Write(_fx.Pc.Registry, Hive.Machine, PerfOptions("other.exe"), "CpuPriorityClass", "dword", "4");
        var page = await Page();
        var row = Assert.Single(page.PriorityRules);
        Assert.Equal(Loc.Instance.Format("Prio_Other", 4), row.CpuText);

        await page.RemovePriorityRuleCommand.ExecuteAsync(row);
        Assert.False(_fx.Dialogs.ConfirmApplyRequests[^1].IsUndo);
        Assert.False(Cpu("other.exe").Existed);
        Assert.Empty(page.PriorityRules);

        // The removal is a change with the old value as its original: undoing it brings the rule back.
        var remove = ProgramPriority.RemoveTweak("other.exe");
        Assert.True(await _fx.Runner.UndoAsync(remove));
        Assert.Equal("4", Cpu("other.exe").Data);
    }

    [Fact]
    public async Task ACancelledRuleKeepsTheForm()
    {
        var page = await Page();
        _fx.Dialogs.ConfirmApplyResult = null;
        page.NewPriorityExe = "game.exe";
        await page.AddPriorityRuleCommand.ExecuteAsync(null);

        Assert.Equal(1, _fx.Dialogs.ConfirmApplyCalls);
        Assert.Equal("game.exe", page.NewPriorityExe);
        Assert.False(Cpu("game.exe").Existed);
        Assert.Empty(page.PriorityRules);
    }

    [Fact]
    public async Task AQuickFixWaitsItsTurnLikeAChange()
    {
        var page = await Page();
        var dns = page.QuickFixRows.Single(r => r.Fix.Id == "Dns");
        static bool Flushed(IEnumerable<string> calls) => calls.Any(c => c.EndsWith("ipconfig.exe /flushdns", StringComparison.OrdinalIgnoreCase));

        // Another change runs: the fix does not start and the page says why.
        ChangeGate.Instance.Busy = true;
        await page.RunQuickFixCommand.ExecuteAsync(dns);
        Assert.False(Flushed(_fx.Pc.Processes.Calls));
        Assert.Contains((Loc.Instance["Change_Busy"], InfoBarSeverity.Warning), _fx.Host.Results);

        ChangeGate.Instance.Busy = false;
        await page.RunQuickFixCommand.ExecuteAsync(dns);
        Assert.True(Flushed(_fx.Pc.Processes.Calls));
        Assert.Contains((Loc.Instance["Quick_DnsDone"], InfoBarSeverity.Success), _fx.Host.Results);
    }
}
