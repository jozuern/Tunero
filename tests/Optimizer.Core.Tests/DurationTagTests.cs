using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimizer.Core.Actions;
using Optimizer.Core.Catalog;
using Optimizer.Core.Tools;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>
/// "Takes minutes" comes from the action types, so a new tweak of that kind gets the tag, its confirmation sentence and
/// the banner note by itself, and work that is done in seconds never claims it.
/// </summary>
public class DurationTagTests
{
    /// <summary>
    /// Only the DISM actions take minutes: features and capabilities install or remove packages, and every DISM call
    /// waits while Windows installs its own updates (reserved storage too). A new action type that takes minutes must be
    /// added here on purpose, with a reason.
    /// </summary>
    [Fact]
    public void OnlyDismActionsTakeMinutes()
    {
        var slow = typeof(TweakAction).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)
            .Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Where(t => ((TweakAction)Activator.CreateInstance(t)!).TakesMinutes)
            .ToHashSet();
        Assert.Equal(new HashSet<Type> { typeof(OptionalFeatureAction), typeof(OptionalCapabilityAction), typeof(ReservedStorageAction) }, slow);
    }

    [Fact]
    public void EveryFeatureSwitchTakesMinutesBothWays()
    {
        foreach (var f in CatalogData.Current.Features.Features)
        foreach (var on in new[] { true, false })
        {
            var t = f.Capability ? OptionalCapabilityAction.Tweak(f, on) : OptionalFeatureAction.Tweak(f, on);
            Assert.True(t.TakesMinutes, t.Id);
        }
    }

    [Fact]
    public void ATweakTakesMinutesWhenAnyOfItsActionsDoes()
    {
        var registry = new RegistryAction { Hive = Hive.Machine, Path = @"SOFTWARE\TuneroTest", Name = "A", Value = JsonSerializer.SerializeToElement(1) };
        var feature = new OptionalFeatureAction { Feature = "TFTP", Enabled = true };
        Assert.False(new TweakDefinition { Id = "t.fast", Category = "Test", Impact = new ImpactInfo(), Actions = [registry] }.TakesMinutes);
        Assert.True(new TweakDefinition { Id = "t.mixed", Category = "Test", Impact = new ImpactInfo(), Actions = [registry, feature] }.TakesMinutes);
    }

    /// <summary>
    /// Registry values, services, power settings and the like are done in seconds: in the catalog only reserved storage
    /// gets the tag, through its action type and not through a flag on the entry.
    /// </summary>
    [Fact]
    public void InTheCatalogOnlyReservedStorageTakesMinutes() =>
        Assert.Equal(["storage.reservedStorageOff"], TweakCatalog.Current.Tweaks.Where(t => t.TakesMinutes).Select(t => t.Id));

    /// <summary>chkdsk /scan is the only quick fix that runs for minutes; restarting a service or flushing a cache does not.</summary>
    [Fact]
    public void OnlyTheDriveScanQuickFixTakesMinutes() =>
        Assert.Equal(["DiskScan"], QuickFixes.All.Where(f => f.TakesMinutes).Select(f => f.Id));
}
