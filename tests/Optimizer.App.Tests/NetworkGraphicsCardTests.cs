using Optimizer.App.ViewModels;
using Optimizer.Core.Hardware;

namespace Optimizer.App.Tests;

/// <summary>
/// The graphics part of Graphics &amp; DNS: the NVIDIA settings for an NVIDIA card, "No NVIDIA graphics card found" only
/// when the scan read the cards and none is NVIDIA. A failed card read (no list) says nothing untrue on a GeForce PC.
/// </summary>
public sealed class NetworkGraphicsCardTests
{
    private static GpuInfo Gpu(string name, Vendor vendor) =>
        new(name, vendor, @"PCI\VEN_0000&DEV_0000\1", null, null, null, null, null, GpuKind.Discrete, null, null, null, 0, TriState.Unknown);

    [Fact]
    public void OnlyAReadListWithoutNvidiaSaysNoNvidia()
    {
        Assert.Equal((true, false), NetworkViewModel.NvidiaCards([Gpu("Intel(R) UHD Graphics 770", Vendor.Intel), Gpu("NVIDIA GeForce RTX 4070", Vendor.Nvidia)]));
        Assert.Equal((false, true), NetworkViewModel.NvidiaCards([Gpu("AMD Radeon RX 7800 XT", Vendor.Amd)]));
        // The card read failed or timed out (the scan keeps no list), or found nothing: neither card.
        Assert.Equal((false, false), NetworkViewModel.NvidiaCards(null));
        Assert.Equal((false, false), NetworkViewModel.NvidiaCards([]));
    }
}
