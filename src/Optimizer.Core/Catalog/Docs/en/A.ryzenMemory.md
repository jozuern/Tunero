# Ryzen memory speed

## Summary
::: variant aboveSync
Memory runs at {{speed}} MT/s, above the range where the Ryzen memory controller usually runs in sync. Check that it did not switch to half speed.
:::
::: variant slowKit
Memory runs at {{speed}} MT/s, below the range Ryzen usually runs best with ({{low}} to {{high}} MT/s).
:::
::: variant default
Checks whether the memory speed fits the Ryzen memory controller.
:::

## Why it matters
On Ryzen, the memory controller and the connection between the processor's dies (Infinity Fabric) run at clocks tied to the memory clock, so both memory speed and memory latency matter for games.
::: if am4
On AM4, testers commonly report that the Infinity Fabric clock (FCLK) can follow the memory clock 1:1 up to about DDR4-3600, on many Zen 2 and Zen 3 processors up to about DDR4-3800. Above that it usually switches to a 2:1 ratio, which adds latency. These limits are not an AMD specification and differ between processors.
:::
::: if am5
On AM5, testers commonly report that the memory controller clock (UCLK) runs 1:1 with the memory clock by default up to about DDR5-6000, and that the BIOS switches it to half speed (1:2) above that, which adds latency. This is not an AMD specification and depends on the processor and BIOS.
:::
AMD's official memory specification with two modules is DDR4-3200 for Ryzen 5000 [1], DDR5-5200 for Ryzen 7000 [2] and DDR5-5600 for Ryzen 9000 [3]. Faster speeds use an EXPO or XMP profile, which AMD describes as memory overclocking [4]. With four DDR5 modules, AMD specifies DDR5-3600 [2][3].

## How we detected it
We read the configured memory speed and the rated speed from the module part numbers. The lower end of the range is AMD's official two-module speed for Ryzen 5000 (DDR4-3200) [1] and Ryzen 7000 (DDR5-5200) [2]; for other Ryzen processors it is a guide value (Ryzen 9000, for example, is specified for DDR5-5600 [3]). The upper end is a typical 1:1 limit reported by testers. If the speed is below the range and the kit's rated speed is unknown, the result is "Unknown", because a slow kit and a memory profile that is off look the same. With four DDR5 modules there is no upgrade advice, because AMD specifies a lower speed for four modules. The actual FCLK or UCLK ratio cannot be read by Windows, so this is guidance, not a measurement.

## How to fix
::: variant aboveSync
1. **BitLocker:** before you change BIOS settings, check whether BitLocker or device encryption is on. If so, suspend protection first (Start > **Manage BitLocker** > **Suspend protection**) or have the recovery key ready; it is often saved in your Microsoft account. A BIOS update or a change to the TPM or the boot configuration can make Windows ask for it at the next start [5][6].
::: if am4
2. In the BIOS, check that **FCLK** is set to half the memory speed (for DDR4-4000: 2000 MHz). If the system is not stable that way, a lower memory speed that keeps 1:1 can be the better choice.
:::
::: if am5
2. In the BIOS, look for **UCLK DIV1 MODE** (wording varies) and set **UCLK = MEMCLK**. If the system is not stable that way, a lower memory speed that keeps 1:1 can be the better choice.
:::
:::
::: variant slowKit
1. For an upgrade, a kit in the range {{low}} to {{high}} MT/s with low CL timings fits this platform. Speeds above AMD's specification need the kit's EXPO or XMP profile [4]. Use two modules in the slots the manual recommends.
:::
::: variant default
No action needed.
:::

## How to check the fix
Run the scan again. For the clock ratio, tools such as ZenTimings or the BIOS show FCLK, UCLK and MCLK.

## Sources
1. https://www.amd.com/en/products/processors/desktops/ryzen/5000-series/amd-ryzen-7-5800x3d.html
2. https://www.amd.com/en/products/processors/desktops/ryzen/7000-series/amd-ryzen-7-7800x3d.html
3. https://www.amd.com/en/products/processors/desktops/ryzen/9000-series/amd-ryzen-7-9800x3d.html
4. https://www.amd.com/en/products/processors/technologies/expo.html
5. https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
6. https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/operations-guide
