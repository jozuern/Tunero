# Intel Application Optimization (APO)

## Summary
::: status Info
{{cpu}} supports Intel APO, which changes how supported games use the cores. It needs the Dynamic Tuning driver; the APO app is optional.
:::
::: status Ok,Unknown,Problem,Unsupported
Checks whether the processor supports Intel Application Optimization.
:::

## Why it matters
Intel APO directs how a supported game's threads are spread over performance and efficient cores. Intel publishes a list of supported games and says APO may improve their performance; it works only for the titles on that list [1]. Intel names verified processors, for example Core i5-14600K, i7-14700K, i9-14900K, Core Ultra 200S K models and some mobile HX and H models; other 12th gen and newer processors get only limited support [1]. APO runs as part of the Intel Dynamic Tuning Technology (DTT) driver, which comes from the PC or mainboard maker [1].
::: variant dttFound
We found services that look like the Intel Dynamic Tuning Technology driver's. Their names come from our own list, which is not confirmed on real hardware yet, so the driver is probably installed, but not certainly.
:::
::: variant dttNotFound
We did not find the Intel Dynamic Tuning Technology services we look for. That does not prove the driver is missing: it may use a name we do not know.
:::

## How we detected it
We match the processor name with the list of verified models and look for the Dynamic Tuning services. Whether APO is active cannot be read reliably.

## How to fix
::: ifnot laptop
1. Install the **Intel Dynamic Tuning Technology** driver from the support page of your mainboard ({{board}}). It is board specific, so it does not come from Intel directly [1].
:::
::: if laptop
1. Install the **Intel Dynamic Tuning Technology** driver from the laptop maker's support page or app. It is specific to the device, so it does not come from Intel directly [1].
:::
2. **BitLocker:** if BitLocker or device encryption is on, suspend protection first (Start > **Manage BitLocker** > **Suspend protection**) or have the recovery key ready; it is often saved in your Microsoft account. A BIOS update or a change to the TPM or the boot configuration can make Windows ask for it at the next start [2][3].
3. Keep the BIOS current and check that Intel Dynamic Tuning Technology or Intel Platform Innovation Framework (IPF) is enabled in it. Most systems have IPF on by default; the menu name differs between boards [1].
4. Optional: download the **Intel Application Optimization** app from the Intel Download Center to see the supported games and turn APO on or off per game [1].

## How to check the fix
The APO app lists the supported games and shows whether APO is active.

## Sources
1. https://www.intel.com/content/www/us/en/support/articles/000095419/processors.html
2. https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
3. https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/operations-guide
