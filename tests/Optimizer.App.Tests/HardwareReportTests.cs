using Optimizer.App.Services;
using Optimizer.App.ViewModels;
using Optimizer.Core.Hardware;
using Optimizer.Core.Hardware.Probes;
using Optimizer.Core.Platform;
using Xunit;

namespace Optimizer.App.Tests;

/// <summary>The System info page's display names: a monitor's own name, else one that tells the displays apart.</summary>
public class HardwareReportTests
{
    private static readonly BuildInfo Os = new(26300, 0, "26H2", "Professional", "Windows 11 Pro", CpuArchitecture.X64, false, null);

    private static DisplayInfo Display(string name, bool internalPanel = false) =>
        new(@"\\.\DISPLAY1", name, "", internalPanel, 10, 1920, 1080, new RefreshRate(60000, 1000), 60, [], "", Vendor.Nvidia, "NVIDIA GeForce RTX 2070",
            false, false, null);

    private static HwTile DisplayTile(params DisplayInfo[] displays) =>
        HardwareReport.Tiles(new HardwareProfile { Os = Os, Displays = displays }, Loc.Instance).Single(t => t.Icon == Wpf.Ui.Controls.SymbolRegular.Desktop24);

    [Fact]
    public void LaptopPanelWithoutANameIsTheBuiltInDisplay()
    {
        var tile = DisplayTile(Display(DisplayProbe.UnnamedDisplay, internalPanel: true));
        Assert.Equal(Loc.Instance["Hw_Display"], tile.Caption);
        Assert.Equal(Loc.Instance["Hw_BuiltInDisplay"], tile.Value);
    }

    [Fact]
    public void UnnamedMonitorsAreNumberedAndNamedOnesKeepTheirName()
    {
        var tile = DisplayTile(Display("DELL U2720Q"), Display(DisplayProbe.UnnamedDisplay), Display(""));
        Assert.Equal(Loc.Instance["Hw_Displays"], tile.Caption);
        Assert.Equal(string.Join("\n", "DELL U2720Q", Loc.Instance.Format("Hw_DisplayNumber", 2), Loc.Instance.Format("Hw_DisplayNumber", 3)), tile.Value);
    }

    [Fact]
    public void SectionTitleNamesTheDisplay()
    {
        var sections = HardwareReport.Build(new HardwareProfile { Os = Os, Displays = [Display("DELL U2720Q"), Display(DisplayProbe.UnnamedDisplay, internalPanel: true)] },
            Loc.Instance);
        Assert.Contains(sections, s => s.Title == $"{Loc.Instance["Hw_Display"]}: DELL U2720Q");
        Assert.Contains(sections, s => s.Title == Loc.Instance["Hw_BuiltInDisplay"]);
    }
}
