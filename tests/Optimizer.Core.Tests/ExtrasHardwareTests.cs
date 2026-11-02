using System.Text.Json;
using Optimizer.Core.Catalog;
using Optimizer.Core.Hardware;
using Optimizer.Core.Hardware.Probes;
using Optimizer.Core.Interop;
using Xunit.Abstractions;

namespace Optimizer.Core.Tests;

/// <summary>Read-only checks of the extra probes on this machine (Category=Hardware).</summary>
public class ExtrasHardwareTests(ITestOutputHelper output)
{
    /// <summary>Headings and tiles show the model without what the brand string adds (trademarks, clock, "CPU").</summary>
    [Theory]
    [InlineData("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", "Intel Core i7-9700K")]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", "13th Gen Intel Core i7-13700K")]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K", "Intel Core Ultra 7 265K")]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor           ", "AMD Ryzen 7 7800X3D")]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X Elite - X1E78100 - Qualcomm Oryon")]
    [InlineData("CPU", "CPU")]
    public void CpuShortNameDropsTheBrandStringExtras(string name, string expected) => Assert.Equal(expected, CpuInfo.Shorten(name));

    /// <summary>A graphics adapter's name in headings and tiles loses its trademark signs, as the processor's does, and nothing else.</summary>
    [Theory]
    [InlineData("Intel(R) Graphics", "Intel Graphics")]
    [InlineData("Intel(R) UHD Graphics 630", "Intel UHD Graphics 630")]
    [InlineData("AMD Radeon(TM) Graphics", "AMD Radeon Graphics")]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", "NVIDIA GeForce RTX 4070 Ti SUPER")]
    [InlineData("Qualcomm\u00AE Adreno\u2122 X1-85 GPU", "Qualcomm Adreno X1-85 GPU")]
    [InlineData("(R)", "(R)")]
    public void GpuShortNameDropsOnlyTheTrademarkSigns(string name, string expected) => Assert.Equal(expected, HardwareNames.WithoutTrademarks(name));

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task ExtrasOnThisPc()
    {
        var profile = await new HardwareScanner(CatalogData.Current).ScanAsync();
        output.WriteLine(JsonSerializer.Serialize(profile.Extras, new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotNull(profile.Extras);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void WlanBssLayoutGivesRealFrequencies()
    {
        var networks = Wlan.VisibleNetworks();
        output.WriteLine($"{networks.Count} access points visible");
        foreach (var n in networks.Take(20)) output.WriteLine($"{n.Bssid} {n.CenterFrequencyKhz} kHz {Wlan.Band(n.CenterFrequencyKhz)} '{n.Ssid}'");
        // Every frequency must be a real Wi-Fi channel center (2.4 / 5 / 6 GHz bands), proving the struct offsets.
        Assert.All(networks, n => Assert.True(n.CenterFrequencyKhz is >= 2_400_000 and <= 7_200_000, $"{n.CenterFrequencyKhz}"));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void NvmlReadsWithoutError()
    {
        foreach (var s in Nvml.Read())
            output.WriteLine($"{s.Name}: {s.TempC} C, {s.GraphicsMhz} MHz, {s.UtilizationPct} %, {s.PowerW} W, PCIe Gen {s.PcieGen} x{s.PcieWidth} (max Gen {s.PcieMaxGen}), reasons 0x{s.Reasons:X}");
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("4", 100)]
    [InlineData("6", 1000)]
    [InlineData("10", 100_000)]
    [InlineData("2500", 2500)]
    [InlineData("999", null)]
    public void SpeedDuplexMapping(string value, int? mbps) => Assert.Equal(mbps, ExtrasProbe.SpeedDuplexMbps(value));
}
