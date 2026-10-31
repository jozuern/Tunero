using Optimizer.Core.Hardware.Probes;
using Xunit;

namespace Optimizer.Core.Tests;

/// <summary>
/// The HDR state from DisplayConfigGetDeviceInfo (wingdi.h 10.0.26100): an SDR display with Auto Color Management (wide
/// color gamut) has "advanced color" on, which is not HDR.
/// </summary>
public class DisplayProbeTests
{
    [Theory]
    [InlineData(0x10u | 0x1u | 0x2u, 2u, true, true)]   // HDR display, HDR on
    [InlineData(0x10u | 0x1u, 0u, true, false)]         // HDR display, HDR off (SDR mode)
    [InlineData(0x40u | 0x1u | 0x2u, 1u, false, false)] // SDR display with Auto Color Management: wide color mode
    [InlineData(0u, 0u, false, false)]
    public void SecondVersionNamesHdrItself(uint value, uint mode, bool supported, bool enabled) =>
        Assert.Equal((supported, enabled), DisplayProbe.HdrFromInfo2(value, mode));

    [Theory]
    [InlineData(0x1u | 0x2u, true, true)]          // advanced color on, not wide color: HDR
    [InlineData(0x1u, true, false)]
    [InlineData(0x1u | 0x2u | 0x4u, true, false)]  // wide color enforced: on, but not HDR
    [InlineData(0u, false, false)]
    public void FirstVersionLeavesWideColorOut(uint value, bool supported, bool enabled) =>
        Assert.Equal((supported, enabled), DisplayProbe.HdrFromInfo1(value));
}
