# Third-party notices

Components shipped in Tunero.exe. Their full license texts are embedded in the exe and shown under Settings > About > Licenses (source: `src/Optimizer.App/Licenses/`, checked against the committed `packages.lock.json` by the test `EveryShippedPackageHasItsLicense`).

| Component | License | Use |
|---|---|---|
| WPF-UI 4.3 and WPF-UI.Abstractions 4.3 (lepoco) | MIT | Window chrome, Fluent controls (NavigationView, cards, toggle switches, info bars), theme |
| CommunityToolkit.Mvvm | MIT | MVVM source generators |
| Markdig | BSD-2-Clause | Markdown parsing for explanation pages |
| System.Management | MIT (.NET) | WMI access |
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0 | Opt-in sensor readings on the Health page. Unmodified; source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| PawnIO Modules (12 compiled modules embedded as resources in LibreHardwareMonitorLib 0.9.6: AMDFamily0F, AMDFamily10, AMDFamily17, IntelMSR, IsaBridgeEC, LpcACPIEC, LpcCrOSEC, LpcIO, RyzenSMU, SmbusI801, SmbusNCT6793, SmbusPIIX4) | LGPL-2.1-or-later | Loaded into the PawnIO driver for processor and mainboard sensors, only after you install PawnIO on the Health page. Unmodified; copyright namazso; source: https://github.com/namazso/PawnIO.Modules |
| DiskInfoToolkit 1.1.2 | MPL-2.0 | Dependency of LibreHardwareMonitorLib (drive sensors). Unmodified; source: https://github.com/Blacktempel/DiskInfoToolkit |
| PCI ID database 2025.11.16 and USB ID database 2025.09.15 (pci.ids, usb.ids, embedded compressed in DiskInfoToolkit 1.1.2) | BSD-3-Clause (offered as GPL-2.0-or-later or BSD-3-Clause) | Device names for drives. Copyright Martin Mares and Albert Pool (pci.ids); maintained by Stephen J. Gowdy (usb.ids). Sources: https://pci-ids.ucw.cz/, http://www.linux-usb.org/usb-ids.html |
| RAMSPDToolkit-NDD 1.4.2 | MPL-2.0 | Dependency of LibreHardwareMonitorLib (memory sensors). Unmodified; source: https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core 1.0.7 | MPL-2.0 | Dependency of DiskInfoToolkit and RAMSPDToolkit. Unmodified; source: https://github.com/Blacktempel/BlackSharp |
| HidSharp 2.6.4 | Apache-2.0 | Dependency of LibreHardwareMonitorLib |
| System.IO.Ports 10.0.3, System.IO.FileSystem.AccessControl 5.0.0 | MIT (.NET) | Dependencies of LibreHardwareMonitorLib |
| Mono.Posix.NETStandard 1.0.0 | MIT | Dependency of LibreHardwareMonitorLib |
| Intel PresentMon 2.6.0 (console, x64) | MIT | Frame time capture for the benchmark. Embedded unmodified, Intel-signed, SHA-256 B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF, checked on extraction. Source: https://github.com/GameTechDev/PresentMon |
| .NET runtime and Windows Desktop runtime 10.0.14 (self-contained, from the SDK pinned in `global.json`) | MIT | Runtime, WPF |

Used through Windows or installed only on request, not shipped:

| Component | License | Use |
|---|---|---|
| NVIDIA NVAPI and NVML (nvapi64.dll, nvml.dll from the NVIDIA driver) | NVIDIA driver license; headers MIT (NVAPI SDK) | Read and write documented driver settings (DRS), VRR state, GPU clock limit reasons. Function IDs and struct layouts from the public SDK headers. |
| PawnIO driver (winget namazso.PawnIO) | GPL-2.0 | Installed only when you choose it on the Health page, for processor and mainboard sensors. Its modules ship inside LibreHardwareMonitorLib (see above). |
| winget (Microsoft App Installer) | MIT (client) | App installs on the Apps page |
| VirusTotal API v3 | VirusTotal terms of service | Opt-in hash lookups with the user's own API key |

Sources of tweak and detection facts (registry paths, values and commands are facts and may be reused; no text or
scripts are copied from GPL projects):

| Project | License | Note |
|---|---|---|
| Chris Titus Tech WinUtil | MIT | Cross-check for the values of privacy.telemetryOff, privacy.activityHistoryOff, privacy.consumerFeaturesOff, network.deliveryOptimizationP2POff, visual.bestPerformance and explorer.fileExtensions, which cite Microsoft documentation; source or cross-check for the undocumented values of gpu.gameDvrOff, explorer.classicContextMenu and explorer.endTask, which are marked as such in the app. Values are reused; descriptions are our own. |
| O&O ShutUp10++ | Freeware, closed source | Idea for the privacy core set only. The privacy entries use Group Policy and Policy CSP values or other values named in Microsoft's own documentation, cited per entry; the few values Microsoft does not document are marked as undocumented in the app. |
| Microsoft PC Manager, Wintoys | Freeware, closed source | Feature ideas only (cleanup categories, storage analyzer, repair tools). Nothing copied. |
| Sysinternals Autoruns | Sysinternals license | Feature idea only (autostart locations, signature check). Nothing copied. |
| Atlas OS playbook | GPLv3 | Reference only. Nothing copied verbatim. |
| hellzerg Optimizer / OptimizerNXT | GPL-3.0 | Reference only. Nothing copied. |

AMD ADLX is not used: it is a C++ SDK and would need a native helper inside the exe, so AMD Software settings are explained (advisor item "AMD Software settings") instead of changed.
