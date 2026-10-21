using Optimizer.Core.Actions;
using Optimizer.Core.Debloat;

namespace Optimizer.Core.Tests;

/// <summary>Whether Windows lets the user remove Edge: its uninstall entry, the device region and Windows' region policy.</summary>
public class EdgeTests
{
    // The shape of System32\IntegratedServicesRegionPolicySet.json on build 26300, cut down to two policies.
    private const string Policy = """
        {
          "$schema": "IntegratedServicesRegionPolicySet.schema.json",
          "policies": [
            { "$comment": "Edge is uninstallable.", "guid": "{1bca278a-5d11-4acf-ad2f-f9ab6d7f93a6}", "defaultState": "disabled",
              "conditions": { "region": { "enabled": ["AT", "DE", "CH", "NO", "YT"] } } },
            { "$comment": "Promotion of Edge", "guid": "{2bf706de-6dbb-4692-b7ef-84d80c47e927}", "defaultState": "enabled",
              "conditions": { "region": { "disabled": ["DE"] } } }
          ]
        }
        """;

    private const string DeviceRegion = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\DeviceRegion";

    private static string? Iso2(int geoId) => geoId switch { 94 => "DE", 244 => "US", _ => null };

    [Theory]
    [InlineData("DE", true)]
    [InlineData("de", true)]
    [InlineData("CH", true)]
    [InlineData("US", false)]
    [InlineData("GB", false)]
    public void RegionPolicyDecides(string region, bool allowed) => Assert.Equal(allowed, Edge.Allows(Policy, region));

    [Fact]
    public void UnknownPolicyOrFileIsNoAnswer()
    {
        Assert.Null(Edge.Allows("""{ "policies": [] }""", "DE"));
        Assert.Null(Edge.Allows("not json", "DE"));
        Assert.Null(Edge.Allows("""{ "other": 1 }""", "DE"));
    }

    [Fact]
    public void AllowedOnlyWhereWindowsOffersUninstall()
    {
        using var r = new SandboxRegistry();
        // Not installed: nothing to remove, whatever the region.
        RegistryValue.Write(r, Hive.Machine, DeviceRegion, "DeviceRegion", "dword", "94");
        var none = Edge.Read(r, Policy, Iso2);
        Assert.False(none.Installed);
        Assert.Equal("block.edgeNotInstalled", none.BlockKey);

        RegistryValue.Write(r, Hive.Machine, Edge.UninstallKey, "DisplayName", "string", "Microsoft Edge");
        var germany = Edge.Read(r, Policy, Iso2);
        Assert.Equal("DE", germany.Region);
        Assert.True(germany.WindowsAllows);
        Assert.Null(germany.BlockKey);

        RegistryValue.Write(r, Hive.Machine, DeviceRegion, "DeviceRegion", "dword", "244");
        var us = Edge.Read(r, Policy, Iso2);
        Assert.False(us.WindowsAllows);
        Assert.Equal("block.edgeNotAllowed", us.BlockKey);

        // NoRemove: Settings offers no Uninstall either, in any region.
        RegistryValue.Write(r, Hive.Machine, DeviceRegion, "DeviceRegion", "dword", "94");
        RegistryValue.Write(r, Hive.Machine, Edge.UninstallKey, "NoRemove", "dword", "1");
        Assert.Equal("block.edgeNotAllowed", Edge.Read(r, Policy, Iso2).BlockKey);
    }

    [Fact]
    public void UnknownRegionLeavesItToWindows()
    {
        using var r = new SandboxRegistry();
        RegistryValue.Write(r, Hive.Machine, Edge.UninstallKey, "DisplayName", "string", "Microsoft Edge");
        // No device region, or no policy file: the page cannot tell, so Settings decides.
        var state = Edge.Read(r, Policy, Iso2);
        Assert.Null(state.RegionAllows);
        Assert.True(state.WindowsAllows);
        RegistryValue.Write(r, Hive.Machine, DeviceRegion, "DeviceRegion", "dword", "94");
        Assert.Null(Edge.Read(r, null, Iso2).RegionAllows);
    }

    [Fact]
    public void GeoIdsAsWindowsNamesThem()
    {
        Assert.Equal("DE", Edge.Iso2(94));
        Assert.Equal("US", Edge.Iso2(244));
        Assert.Null(Edge.Iso2(-5));
    }
}
