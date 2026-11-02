# Hardware virtualization off in the BIOS

## Summary
::: variant antiCheat
Hardware virtualization is off in the BIOS. Memory integrity and VBS need it, and installed anti-cheats can ask for either ({{antiCheats}}), so games may refuse to start.
:::
::: variant off
Hardware virtualization (Intel VT-x or AMD-V) is off in the BIOS. Memory integrity and Linux or Android apps on Windows need it.
:::
::: variant default
Checks that hardware virtualization (Intel VT-x or AMD-V) is on in the BIOS. Memory integrity needs it.
:::

## Why it matters
Memory integrity (core isolation) is a feature of virtualization-based security (VBS) and runs in an isolated environment that Windows creates with hardware virtualization [8]. To use it, virtualization must be enabled in the UEFI or BIOS [1]. Some anti-cheats check these features: Riot Vanguard can ask for memory integrity depending on the PC [2], and FACEIT requires VBS together with IOMMU, rolled out in waves, and asks some players to turn on memory integrity [3][7]. Virtualization also lets Windows run other operating systems, such as Linux or Android apps [4].

Turning virtualization on in the BIOS does not change game performance by itself. Whether you then turn memory integrity on is a separate choice.

## How we detected it
We read from Windows (WMI) whether the processor supports virtualization (`Win32_Processor.VMMonitorModeExtensions`), whether the firmware turned it on (`VirtualizationFirmwareEnabled`) [5] and whether a hypervisor is present (`Win32_ComputerSystem.HypervisorPresent`) [6]. On some PCs the processor values read as off while the Windows hypervisor runs, so a running hypervisor counts as "on".

## How to fix
1. **BitLocker:** before you change BIOS settings, check whether BitLocker or device encryption is on. If so, suspend protection first (Start > **Manage BitLocker** > **Suspend protection**) or have the recovery key ready; it is often saved in your Microsoft account. A BIOS update or a change to the TPM or the boot configuration can make Windows ask for it at the next start [9][10].
2. Restart into the UEFI: **Settings > System > Recovery**, next to **Advanced startup** select **Restart now**, then **Troubleshoot > Advanced options > UEFI Firmware Settings** (on some versions "UEFI Settings") **> Restart** [4]. Many PCs also open it when you press **Del** or **F2** during start.
::: if intel
3. Turn on **Intel Virtualization Technology (VT-x)**, on some boards called **Intel (VMX) Virtualization Technology**, usually on the CPU or Advanced page [4].
:::
::: ifnot intel
3. Turn on **AMD Virtualization (AMD-V)** [4]. Many boards call it **SVM Mode** [7]; search for "SVM" or "virtualization" if the menu differs.
:::
4. Save and exit (usually **F10**).

## How to check the fix
Run the scan again. "Virtualization (Intel VT-x or AMD-V) on" should show "Yes".

## Sources
1. https://support.microsoft.com/en-us/windows/security/windows-security/device-security-in-the-windows-security-app
2. https://support.riotgames.com/riot/performance/vanguard-security-requirements
3. https://support.faceit.com/hc/en-us/articles/23117181142556-Windows-Security-Requirements-FAQ
4. https://support.microsoft.com/en-us/windows/experience/enable-virtualization-on-windows
5. https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-processor
6. https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-computersystem
7. https://support.faceit.com/hc/en-us/articles/22851956652956-Known-issues-with-Anti-Cheat-Requirements
8. https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity
9. https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
10. https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/operations-guide
