# VM test plan

Real apply and undo round trips for every change the app can make. Unit tests only cover the registry sandbox and fakes; this plan covers real Windows. Run it before each public release, in a Hyper-V VM, never on your own PC.

The tweak tables in sections 2 to 5 are generated from the catalog: after a catalog change, run `dotnet test` once with the environment variable `PCO_UPDATE_DOCS=1`.

## Setup

1. Hyper-V VM, generation 2, Windows 11 25H2 or newer x64 (Pro or Home; before a release, once on each, and once on the oldest supported build), Secure Boot and virtual TPM on, 2 or more virtual processors, 16 GB memory (`memory.compressionOff` applies from 16 GB), 80 GB disk. Use an unmodified Microsoft ISO: debloated images change the starting state the tests compare against. See [Creating the VM](#creating-the-vm).
2. Install all Windows updates, sign in with a local administrator account, install Steam or another launcher so game library checks have something to find.
3. Copy the Release exe (`dotnet publish src/Optimizer.App -p:PublishProfile=SingleFile`, then `artifacts/publish/Tunero.exe`) into the VM.
4. Take a checkpoint named **clean**. Go back to it whenever a test leaves the VM in a state you do not trust.
5. Keep `%ProgramData%\Tunero\logs` open: every apply, verify and undo is logged there. Attach the log to any bug report.
6. The app recognizes the VM and leaves out the advice about physical hardware (memory channels and speed, BIOS age, Resizable BAR, PCIe and NVMe links, drive health, microcode): those checks are tested with simulated hardware in the unit tests and on real PCs.

For each tweak: apply it alone, check that the state shows **On** and that the change is really there (Settings, regedit, `powercfg /q`, `sc qc`, `schtasks /query`, `bcdedit`), restart or sign out where the table says so, then **Undo** on the Changes page and check that the original value is back. Write OK or the problem into the Result column.

### Creating the VM

Hyper-V needs Windows 11 Pro on the host: turn on **Hyper-V** in "Turn Windows features on or off" and restart. Then run this in an elevated PowerShell (change `$iso`):

```powershell
$name = 'PCO-Test'; $root = 'C:\Hyper-V'; $iso = "$env:USERPROFILE\Downloads\Win11_x64.iso"
New-VM -Name $name -Generation 2 -MemoryStartupBytes 16GB -Path $root -NewVHDPath "$root\$name\$name.vhdx" -NewVHDSizeBytes 80GB -SwitchName 'Default Switch'
Set-VMMemory -VMName $name -DynamicMemoryEnabled $false
Set-VMProcessor -VMName $name -Count 4 -ExposeVirtualizationExtensions $true
Set-VM -Name $name -CheckpointType Production -AutomaticCheckpointsEnabled $false
Set-VMKeyProtector -VMName $name -NewLocalKeyProtector; Enable-VMTPM -VMName $name
$dvd = Add-VMDvdDrive -VMName $name -Path $iso -Passthru; Set-VMFirmware -VMName $name -FirstBootDevice $dvd
Get-VMIntegrationService -VMName $name | Enable-VMIntegrationService
Add-LocalGroupMember -SID 'S-1-5-32-578' -Member ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
```

`ExposeVirtualizationExtensions` lets memory integrity run in the VM, so `security.vbsOff` has something to turn off. Membership in Hyper-V Administrators (SID `S-1-5-32-578`, active after the next sign-in) lets the commands below run without UAC.

1. Open **Hyper-V Manager**, double-click the VM, click **Start** and press a key at "Press any key to boot from CD or DVD". If you miss it, the VM shows a boot error: use **Action > Reset** and try again.
2. Install Windows. For a local account, press Shift+F10 on the network or account screen and run `start ms-cxh:localonly`. If newer builds remove that route, sign in with a Microsoft account, add a local account in Settings > Accounts > Other users ("Add a user without a Microsoft account"), make it an administrator, and use only that account. Give it a password: the enhanced session (shared clipboard, window resizing) needs one.
3. An unactivated Windows works for these tests, but Settings > Personalization is locked; check those values in regedit instead.
4. Copy the exe from the host: `Copy-VMFile -Name PCO-Test -SourcePath artifacts\publish\Tunero.exe -DestinationPath C:\Test\Tunero.exe -CreateFullPath -FileSource Host` (the VM must be running), or copy and paste the file in an enhanced session.
5. Checkpoints: `Checkpoint-VM -Name PCO-Test -SnapshotName clean` to take one, `Restore-VMCheckpoint -VMName PCO-Test -Name clean -Confirm:$false` to go back (or right-click the VM or the checkpoint in Hyper-V Manager). Production checkpoints do not keep the running state: after a restore the VM is off and starts fresh.

## 1. General flow

| Step | Expected | Result |
|---|---|---|
| First start | UAC prompt, scan finishes, a profile is suggested, no error banner | OK Home 2026-10-14 (26H2 Core 26300.9711, 8 GB, German Windows, PCO-Home): window after 5 to 30 s, scan shown 3 to 5 s later, Older PC suggested, the Start menu question added the entry, no error banner. On the first run after setup (Windows still servicing) the tweak states took 36 s, DISM alone 66 s |
| Settings > About > Licenses | Window opens, every component shows its text | OK Home 2026-10-14 (26H2 Core 26300.9711, 8 GB, German Windows, PCO-Home): runtime 10.0.13 listed |
| Apply recommended | One confirmation listing each item with its reason; no Expert, boot-critical or anti-cheat sensitive item in it | OK Home 2026-10-14 (26H2 Core 26300.9711, 8 GB, German Windows, PCO-Home): 4 safe items with every value. "Widgets off" failed: Windows denies the policy value while the User Choice Protection Driver runs (with its service disabled the write worked); the tweak is now offered only without that driver |
| Restore point | Created once before the first change of the session (System Protection on), or the app asks to turn it on | OK Home 2026-10-14 (26H2 Core 26300.9711, 8 GB, German Windows, PCO-Home): System Protection is off on Home, the app asked (Continue without, Turn on and continue) |
| After apply | Every item verified as On, all listed on the Changes page | OK Home 2026-10-14 (26H2 Core 26300.9711, 8 GB, German Windows, PCO-Home): the 3 that applied are On and listed, the failed one is not |
| Restart | States still On; items marked "after restart" now active | OK Home 2026-10-14 (26H2 Core 26300.9711, 8 GB, German Windows, PCO-Home) |
| Undo all | Every original value back (spot check 5 in regedit), Changes page empty | OK Home 2026-10-14 (26H2 Core 26300.9711, 8 GB, German Windows, PCO-Home): the change log lists each value back to its original, Changes page and backups empty |
| Progress banner | Apply recommended with several items, then Undo all: a card above the page shows the restore point step, then "1 of n" to "n of n" with each change's title; Stop during Apply recommended ends it after the change that runs, and the result names the ones not started. Cleanup counts its categories, SFC and DISM fill the bar from their percentage, Update all apps counts winget's packages when there are updates (check that winget prints "(1/n)") | |
| Restart again | Windows starts normally, scan shows the same state as before the apply | OK Home 2026-10-14 (26H2 Core 26300.9711, 8 GB, German Windows, PCO-Home): the same 4 recommendations as before the apply |
| Reset detection | Apply "Show file extensions", turn it off by hand in Explorer options, scan again: banner says the change was reset, "Apply again" works | |
| Windows update between apply and undo | Checkpoint, apply recommended, install a cumulative update, scan: reset changes are listed with the update as likely cause; Undo still restores the originals | |
| Language and theme | Switch to Deutsch and Dark in Settings, restart the app: both kept | |
| Update check | Turn on, restart the app: no error; with a newer published release a banner links to the release page | |
| Code signature (first signed release) | Download the release in Edge: Properties > Digital Signatures names the publisher and a timestamp, `Get-AuthenticodeSignature` says Valid; the UAC prompt shows "Tunero" and the verified publisher instead of "Unknown publisher". Note what SmartScreen shows on the first start (it may still warn while the certificate has no reputation) | |
| Smart App Control (first signed release) | On a VM with Smart App Control on (Windows Security > App & browser control > Smart App Control settings; current updates can turn it on again without a clean install where Windows offers it): the signed exe starts; an unsigned build of the same version is blocked | |
| 24H2 | On a 24H2 VM (build 26100): the app starts with the note that 24H2 is no longer supported; Undo of a change made there works | |
| Small screen | Display at 1920 x 1080 and 150 % scaling: the window fits above the taskbar (footer and result bar visible); a confirmation with many changes (Apply recommended) keeps its buttons on screen. Re-run since the Storage page joined the rail (twelve pages): every page including Manual checks stays fully above the footer line, with the compact item height (34 px) | OK 2026-10-13 (26H2, basic session): the window opens maximized above the taskbar with the rail, Apply recommended keeps Apply and Cancel on screen; the rail cut off Manual checks, fixed with compact items |
| High contrast | Turn on a contrast theme (Settings > Accessibility > Contrast themes) while the app runs: it switches to Windows' colors, every page stays readable; turn it off: Light or Dark again | OK 2026-10-13 (26H2, enhanced session): Overview, Tweaks, Changes and Settings in the contrast colors, Light again afterwards; the accent text ("Recommended" lines) was dark blue on black, fixed to Windows' link color |
| Navigation for assistive tools | UI Automation (Inspect, or a script): every page in the rail is a list item with Invoke and SelectionItem, Select opens the page and the open page reads as selected; the three group headings are text with heading level 2. Narrator on the rail says the page names and "selected" | OK 2026-10-14 (26H2, published exe, script): 14 pages and 3 headings; Select and Invoke opened Settings and Tweaks. The automation tree of seven pages was otherwise the same as before the switch. Narrator itself still to hear |
| Sources | Select a tweak, click a source at the end of its page: it opens in the browser of the signed-in user; Tab reaches the links | OK 2026-10-13 (26H2): Tab from the explanation to source 1, Enter opens it in Edge as the signed-in user, not elevated |
| Install on the first start | Signed test build in Downloads, first start: the setup screen covers the window (EN and DE). Install: `C:\Program Files\Tunero\Tunero.exe` exists, users may read and run it but not change it (`icacls`), the copy in Downloads is gone, the installed copy opens with "installed" in the result bar, the Start menu entry starts it, the taskbar shows the icon, Settings offers the reapply task. Settings > Apps > Installed apps lists Tunero with version, publisher Joshua Zürn and the icon | OK 2026-10-15 (26H2 Pro, signed test build 1.2.14 in Downloads: the setup screen covered the window; Install copied it to `C:\Program Files\Tunero\Tunero.exe` with SYSTEM and Administrators full, Users read and run, removed the Downloads copy, started the installed copy, moved the Start menu entry there; Installed apps entry: Tunero 1.2.14, Joshua Zürn, icon, `--uninstall`; taskbar icon shown; Settings offered the reapply task and no longer the install. EN only) |
| Install refused | Put `version.dll` into an existing `C:\Program Files\Tunero` first: Install stops with the folder step named, nothing is copied; an unsigned build shows no setup screen, only the Start menu question | OK 2026-10-15 for the planted `version.dll`: "Tunero was not installed (the folder in Program Files)", nothing copied, no entry, the Downloads copy kept. The unsigned build: not run in the VM (covered by tests) |
| Run from here | Run from here: the Overview opens, the next start shows no setup; Settings > Install in Program Files opens the screen again | OK 2026-10-15: SetupDeclined saved, the next start opened the Overview without the setup |
| Installed copy updated and removed | Update now on the installed copy: Installed apps shows the new version. Uninstall from Windows Settings > Apps: Tunero opens with its uninstall question; afterwards the folder, the Installed apps entry and the Start menu entry are gone. Same from Settings > Uninstall | Partly 2026-10-15: `--uninstall` while Tunero was open showed "Tunero is open. Uninstall it there" and changed nothing; with Tunero closed it asked, then removed `C:\Program Files\Tunero`, the Installed apps entry, the Start menu entry and the data folder. Update now on an installed copy: needs a newer release |
| winget and the entry | With an installed copy: `winget list Tunero` and `winget upgrade`: note whether winget takes the entry for `jozuern.Tunero`, and what it would do | 2026-10-15: `winget list Tunero` shows `ARP\Machine\X64\Tunero 1.2.14` (not correlated: jozuern.Tunero is not in winget-pkgs yet), `winget upgrade` offers nothing. Check again once the package is merged |
| One window | Start the published exe, minimize it, start it again from the Start menu entry: the open window comes back to the front and no second Tunero process stays (Task Manager); `Tunero.exe --apply <file>` still runs beside it. Update now from the previous version: the new version opens once the old one has closed (it starts with `--after-update` and waits up to 30 s) | |
| Failed self-update start | Update with the new exe blocked (an antivirus quarantine, or delete the new exe in the second between install and start under a debugger): the previous version is put back and the message says so | |
| Update now from 1.0.0 | Start the new exe elevated with `TUNERO_RUNTIME_RESTARTED=1` in its environment (what 1.0.0's Update now passes on), for example with `--export <file>`: it starts and writes the file | OK 2026-10-14 (26H2, PowerShell Direct): exit 0 and the file; the build before the fix refused with exit 8 and wrote nothing |

<!-- Generated from the tweak catalog by DocsConsistencyTests. Do not edit by hand. -->

The tables below cover all 235 catalog tweaks. Preview tweaks are the risky ones; test each of them on its own.

## 2. Expert and boot-critical tweaks

Turn on Expert mode. Take a checkpoint before **each** of these, apply it alone, restart, confirm Windows still starts, then undo and restart again.

Preconditions, or there is nothing to change on a clean install: turn on memory integrity (Windows Security > Device security > Core isolation) and restart before `security.vbsOff`; run `bcdedit /set {current} useplatformclock true` before `leftover.usePlatformClock`.

| Tweak | Title | Risk | Then | Actions | Result |
|---|---|---|---|---|---|
| `security.vbsOff` | Virtualization-based security and memory integrity off | expert, boot-critical, anti-cheat sensitive, preview | restart | registry | |
| `leftover.usePlatformClock` | Remove forced platform clock (useplatformclock) | expert, boot-critical, preview | restart | bcd | |
| `security.deviceEncryptionPrevented` | No automatic device encryption | expert, preview |  | registry | |
| `expert.legacyBootMenu` | Classic F8 boot menu | expert, boot-critical, preview | restart | bcd | |

## 3. Tweaks that need a restart or sign-out

| Tweak | Title | Risk | Then | Actions | Result |
|---|---|---|---|---|---|
| `power.throttlingOff` | Power throttling off | safe | restart | registry | |
| `gpu.hags` | Hardware-accelerated GPU scheduling (HAGS) | safe | restart | registry | |
| `gpu.mpoOff` | Multiplane overlay (MPO) off | moderate, preview | restart | registry | |
| `memory.compressionOff` | Memory compression off | moderate | restart | memoryCompression | |
| `memory.sysmainOff` | SysMain (Superfetch) off | moderate | restart | service | |
| `memory.pagefileSystemManaged` | Page file managed by Windows | moderate | restart | registry | |
| `storage.lastAccessOff` | NTFS last-access timestamps off | safe | restart | registry | |
| `network.throttlingIndex` | Network throttling off (NetworkThrottlingIndex) | safe, preview | restart | registry | |
| `network.preferIpv4` | Prefer IPv4 over IPv6 | safe | restart | registryBits | |
| `privacy.telemetryOff` | Telemetry to minimum | moderate | restart | registry, service, scheduledTask | |
| `visual.bestPerformance` | Visual effects: best performance | safe | sign out | registry, registryBinaryBits | |
| `explorer.classicContextMenu` | Classic right-click menu | safe | sign out | registry | |
| `privacy.appCompatTelemetryOff` | Application compatibility telemetry off | moderate | restart | registry | |
| `privacy.phoneLinkOff` | Phone-PC linking off | moderate | restart | registry | |
| `privacy.crossDeviceOff` | Continue experiences on other devices off | moderate | restart | registry | |
| `system.registryBackup` | Registry backup to the RegBack folder | safe | restart | registry | |
| `ai.recallOff` | Recall removed | moderate | restart | registry | |
| `focus.notificationCenterOff` | Notification center removed from the taskbar | moderate | restart | registry | |
| `personalize.taskbarSearchHidden` | Taskbar: search hidden | safe | sign out | registry | |
| `personalize.taskbarSearchIcon` | Taskbar: search as an icon only | safe | sign out | registry | |
| `personalize.taskViewHidden` | Taskbar: Task View button hidden | safe | sign out | registry | |
| `personalize.startRecentlyAddedHidden` | Start: no recently added apps | safe | sign out | registry | |
| `personalize.startMostUsedHidden` | Start: no most used apps | safe | sign out | registry | |
| `system.longPaths` | Long file paths | safe | restart | registry | |
| `security.lsaProtection` | LSA protection on | moderate | restart | registry | |
| `security.defenderSandbox` | Microsoft Defender in a sandbox | safe | restart | registry | |
| `personalize.desktopThisPc` | This PC icon on the desktop | safe | sign out | registry | |
| `personalize.titleBarShakeOff` | No minimizing by shaking a window | safe | sign out | registry | |
| `input.printScreenSnippingOff` | Print Screen copies the screen again | safe | sign out | registry | |
| `explorer.fullPathTitle` | File Explorer: full path in the title | safe | sign out | registry | |
| `visual.wallpaperFullQuality` | Desktop wallpaper without JPEG recompression | safe | sign out | registry | |
| `system.bsodDetails` | Blue screen shows technical details | safe | restart | registry | |

## 4. Other tweaks

`memory.sysmainOff` cannot be tested in Hyper-V: the virtual disk is not reported as an SSD, so the tweak does not apply there.

| Tweak | Title | Risk | Then | Actions | Result |
|---|---|---|---|---|---|
| `power.balancedPlan` | Switch to the Balanced power plan | safe |  | powerScheme | |
| `power.gamingPlan` | Gaming power plan (based on High performance) | safe |  | powerScheme | |
| `power.ultimatePlan` | Ultimate Performance power plan | moderate |  | powerScheme | |
| `power.turboRestore` | Restore processor turbo | safe |  | powerSetting | |
| `power.usbSelectiveSuspendOff` | USB selective suspend off | safe |  | powerSetting | |
| `power.pcieAspmOff` | PCIe link power management off | safe |  | powerSetting | |
| `power.fastStartupOff` | Fast Startup off | safe |  | registry | |
| `power.hibernateOff` | Hibernation off | safe |  | hibernation | |
| `gpu.windowedOptimizations` | Optimizations for windowed games | safe |  | registryToken | |
| `gpu.gameMode` | Game Mode on | safe |  | registry | |
| `gpu.gameDvrOff` | Game Bar captures off | safe |  | registry | |
| `input.mouseAccelOff` | Mouse acceleration off | safe |  | registry | |
| `storage.trimOn` | TRIM on | safe |  | registry | |
| `storage.storageSenseOn` | Storage Sense on | safe |  | registry | |
| `network.nagleOff` | Delayed TCP acknowledgements off (TcpAckFrequency) | moderate |  | registry | |
| `network.deliveryOptimizationP2POff` | Delivery Optimization peer-to-peer off | safe |  | registry | |
| `privacy.activityHistoryOff` | Activity history off | safe |  | registry | |
| `privacy.consumerFeaturesOff` | Consumer features off | safe |  | registry | |
| `privacy.locationOff` | Location access off | safe |  | registry | |
| `background.backgroundAppsOff` | Background apps off | moderate |  | registry | |
| `background.aiOff` | Recall snapshots off | moderate |  | registry | |
| `background.widgetsOff` | Widgets off | safe |  | registry | |
| `visual.transparencyOff` | Transparency effects off | safe |  | registry | |
| `explorer.fileExtensions` | Show file extensions | safe |  | registry | |
| `explorer.driveLettersFirst` | Drive letters first | safe |  | registry | |
| `explorer.shortcutSuffixOff` | No "- Shortcut" in new shortcut names | safe |  | registry | |
| `explorer.menuGiveAccessOff` | No "Give access to" in the right-click menu | safe |  | registry | |
| `explorer.menuPreviousVersionsOff` | No "Restore previous versions" in the right-click menu | safe |  | registry | |
| `explorer.menuTerminalOff` | No "Open in Terminal" in the right-click menu | safe |  | registry | |
| `explorer.menuCastOff` | No "Cast to Device" in the right-click menu | safe |  | registry | |
| `explorer.menuShareOff` | No "Share" in the right-click menu | safe |  | registry | |
| `explorer.endTask` | "End task" in the taskbar menu | safe |  | registry | |
| `network.dns.cloudflare` | Public DNS servers | safe |  | dns | |
| `network.dns.google` | Public DNS servers | safe |  | dns | |
| `network.dns.quad9` | Public DNS servers | safe |  | dns | |
| `network.dns.opendns` | Public DNS servers | safe |  | dns | |
| `network.dns.adguard` | Public DNS servers | safe |  | dns | |
| `network.dns.automatic` | DNS servers: automatic (from the router) | safe |  | dns | |
| `privacy.advertisingIdOff` | Advertising ID off | safe |  | registry | |
| `privacy.tailoredExperiencesOff` | Tailored experiences off | safe |  | registry | |
| `privacy.feedbackNotificationsOff` | Feedback requests off | safe |  | registry | |
| `privacy.diagnosticLogsLimited` | Limit diagnostic logs and memory dumps | safe |  | registry | |
| `privacy.inkingTypingOff` | Inking and typing personalization off | safe |  | registry | |
| `privacy.onlineSpeechOff` | Online speech recognition off | safe |  | registry | |
| `privacy.webSearchOff` | Web results in Start search off | safe |  | registry | |
| `privacy.searchHighlightsOff` | Search highlights off | safe |  | registry | |
| `privacy.cloudSearchOff` | Cloud content in search off | safe |  | registry | |
| `privacy.cloudClipboardOff` | Clipboard sync across devices off | safe |  | registry | |
| `privacy.suggestionsOff` | Tips, suggestions and welcome screens off | safe |  | registry | |
| `privacy.onlineTipsOff` | Online tips in Settings off | safe |  | registry | |
| `privacy.appLaunchTrackingOff` | App launch tracking off | safe |  | registry | |
| `privacy.languageListOff` | Language list for websites off | safe |  | registry | |
| `privacy.findMyDeviceOff` | Find my device off | moderate |  | registry | |
| `privacy.errorReportingOff` | Windows Error Reporting off | moderate |  | registry | |
| `privacy.ceipOff` | Customer Experience Improvement Program off | safe |  | registry | |
| `privacy.appPersonalDataOff` | Store app access to personal data off | moderate |  | registry | |
| `privacy.settingsSyncOff` | Settings sync off | moderate |  | registry | |
| `privacy.messageSyncOff` | Text message cloud backup off | safe |  | registry | |
| `privacy.mapsTrafficOff` | Offline maps updates off | safe |  | registry | |
| `privacy.clipboardHistoryOff` | Clipboard history off | moderate |  | registry | |
| `privacy.speechModelUpdatesOff` | No automatic speech model updates | safe |  | registry | |
| `services.gamingPreset` | Conservative services preset | safe |  | service | |
| `battery.boostOffDc` | Processor boost off on battery | moderate |  | powerSetting | |
| `battery.wifiPowerSavingDc` | Wi-Fi power saving on battery: maximum | safe |  | powerSetting | |
| `battery.pcieAspmMaxDc` | PCIe power saving on battery: maximum | safe |  | powerSetting | |
| `quiet.boostOff` | Processor boost off (quieter and cooler) | moderate |  | powerSetting | |
| `quiet.powerModeEfficiency` | Power mode: Best power efficiency | safe |  | powerMode | |
| `office.clipboardHistoryOn` | Clipboard history on | safe |  | registry | |
| `office.launchToThisPc` | File Explorer opens to This PC | safe |  | registry | |
| `background.edgeBoostOff` | Microsoft Edge: no startup boost, no background mode | safe |  | registry | |
| `updates.driversExcluded` | Drivers not included with Windows Update | moderate |  | registry | |
| `privacy.deviceMetadataOff` | No automatic download of device apps | safe |  | registry | |
| `edge.diagnosticDataRequired` | Microsoft Edge: only required diagnostic data | safe |  | registry | |
| `edge.personalizationOff` | Microsoft Edge: no browsing data for personalization | safe |  | registry | |
| `edge.shoppingOff` | Microsoft Edge: no shopping features | safe |  | registry | |
| `edge.sidebarOff` | Microsoft Edge: no sidebar | safe |  | registry | |
| `edge.setupPromptsOff` | Microsoft Edge: no setup and default browser prompts | safe |  | registry | |
| `edge.newTabQuiet` | Microsoft Edge: new tab page without news and promotions | safe |  | registry | |
| `edge.quickLinksOff` | Microsoft Edge: no quick links on the new tab page | safe |  | registry | |
| `edge.promotionsOff` | Microsoft Edge: no tips, recommendations and Acrobat offer | safe |  | registry | |
| `edge.trackingPreventionStrict` | Microsoft Edge: strict tracking prevention | moderate |  | registry | |
| `edge.searchSuggestionsOff` | Microsoft Edge: no search and site suggestions while typing | safe |  | registry | |
| `edge.localSuggestionsOff` | Microsoft Edge: no suggestions from history and favorites | safe |  | registry | |
| `edge.errorPageServicesOff` | Microsoft Edge: no web services for error pages | safe |  | registry | |
| `edge.networkPredictionOff` | Microsoft Edge: no network prediction | safe |  | registry | |
| `edge.feedbackOff` | Microsoft Edge: no feedback tool | safe |  | registry | |
| `edge.paymentQueryOff` | Microsoft Edge: sites cannot check for saved payment methods | safe |  | registry | |
| `edge.passwordSavingOff` | Microsoft Edge: no saving of passwords | safe |  | registry | |
| `edge.signInOff` | Microsoft Edge: no browser sign-in | moderate |  | registry | |
| `edge.aiOff` | Microsoft Edge: generative AI features off | safe |  | registry | |
| `edge.cloudWritingOff` | Microsoft Edge: no cloud text prediction and spell checking | safe |  | registry | |
| `edge.tabServicesOff` | Microsoft Edge: no tab organization service | safe |  | registry | |
| `edge.visualSearchOff` | Microsoft Edge: no visual search on images | safe |  | registry | |
| `chrome.aiOff` | Google Chrome: generative AI features off | safe |  | registry | |
| `chrome.aiNoTraining` | Google Chrome: AI features without improving Google's models | safe |  | registry | |
| `chrome.urlDataOff` | Google Chrome: no URL-keyed data collection | safe |  | registry | |
| `chrome.promotionsOff` | Google Chrome: no promotions and price tracking | safe |  | registry | |
| `chrome.backgroundOff` | Google Chrome: no background mode | safe |  | registry | |
| `brave.cryptoOff` | Brave: no Rewards, Wallet and VPN | safe |  | registry | |
| `brave.aiChatOff` | Brave: no Leo AI assistant | safe |  | registry | |
| `brave.telemetryOff` | Brave: no product analytics, usage ping and Web Discovery | safe |  | registry | |
| `brave.extrasOff` | Brave: no News, Talk and Playlist | safe |  | registry | |
| `ai.paintOff` | Paint: Cocreator, generative fill and Image Creator off | safe |  | registry | |
| `ai.notepadOff` | Notepad: AI features off | safe |  | registry | |
| `ai.clickToDoOff` | Click to Do off | safe |  | registry | |
| `perm.cameraOff` | Windows apps: no camera access | safe |  | registry | |
| `perm.microphoneOff` | Windows apps: no microphone access | safe |  | registry | |
| `perm.notificationsOff` | Windows apps: no access to your notifications | safe |  | registry | |
| `perm.voiceActivationOff` | Windows apps: no voice activation | safe |  | registry | |
| `perm.motionOff` | Windows apps: no motion data | safe |  | registry | |
| `perm.phoneOff` | Windows apps: no phone calls | safe |  | registry | |
| `perm.radiosOff` | Windows apps: no control of radios | safe |  | registry | |
| `perm.devicesOff` | Windows apps: no unpaired or trusted devices | safe |  | registry | |
| `perm.screenshotsOff` | Windows apps: no screenshots of other windows | safe, preview |  | registry | |
| `perm.presenceOff` | Windows apps: no presence sensing | safe |  | registry | |
| `privacy.searchConnectedOff` | Windows Search: no web results and no location | safe |  | registry | |
| `privacy.malwareReportOff` | Malicious Software Removal Tool: no infection reports | safe |  | registry | |
| `privacy.thirdPartySpotlightOff` | Windows Spotlight: no third-party suggestions | safe |  | registry | |
| `privacy.spotlightOff` | Windows Spotlight off | safe |  | registry | |
| `privacy.cloudContentOff` | No cloud content, account notices and Windows tips | safe |  | registry | |
| `privacy.oneSettingsOff` | No configuration downloads from OneSettings | moderate |  | registry | |
| `privacy.privacyExperienceOff` | No privacy settings page at sign-in | safe |  | registry | |
| `privacy.recentFilesOff` | No recently opened files in Start, jump lists and File Explorer | safe |  | registry | |
| `privacy.lockScreenCameraOff` | No camera on the lock screen | safe |  | registry | |
| `privacy.drmOnlineOff` | Windows Media DRM: no internet access | safe |  | registry | |
| `privacy.oneDriveFolderBackupOff` | OneDrive: no backup of Desktop, Documents and Pictures | safe |  | registry | |
| `background.oneDriveSyncOff` | OneDrive file sync off | moderate |  | registry | |
| `updates.featureUpdatesDeferred` | Feature updates one year later | safe |  | registry | |
| `updates.storeAutoUpdateOff` | Microsoft Store: no automatic app updates | moderate |  | registry | |
| `updates.activeHoursEvening` | No update restarts from 8 AM to 2 AM | safe |  | registry | |
| `updates.restartWhenYouChoose` | Update restarts when you choose, for up to a week | moderate |  | registry | |
| `updates.qualityUpdatesWeekLater` | Monthly updates one week later | moderate |  | registry | |
| `storage.appArchivingOff` | No automatic archiving of unused apps | safe |  | registry | |
| `focus.updateNotificationsReduced` | Fewer Windows Update notifications | safe |  | registry | |
| `focus.toastsOff` | No pop-up notifications from apps | safe |  | registry | |
| `focus.lockScreenToastsOff` | No app notifications on the lock screen | safe |  | registry | |
| `power.wakeTimersOff` | No wake timers | safe |  | powerSetting | |
| `personalize.logonBlurOff` | Sign-in screen: clear background | safe |  | registry | |
| `personalize.firstLogonAnimationOff` | No first sign-in animation | safe |  | registry | |
| `system.verboseStatus` | Detailed status messages at startup and shutdown | safe |  | registry | |
| `personalize.hibernateInPowerMenu` | Hibernate in the power menu | safe |  | registry | |
| `explorer.openWithPromptsOff` | Open with: no Store search and no new app notices | safe |  | registry | |
| `explorer.shortcutSearchOff` | Broken shortcuts: no drive search | safe |  | registry | |
| `personalize.inkWorkspaceOff` | Windows Ink Workspace off | safe |  | registry | |
| `personalize.defaultPrinterManual` | Default printer stays as you set it | safe |  | registry | |
| `input.accessibilityShortcutsOff` | No Sticky Keys, Filter Keys and Toggle Keys shortcuts | safe |  | accessibilityShortcut | |
| `security.puaProtection` | Microsoft Defender: block potentially unwanted apps | safe |  | registry | |
| `security.networkProtection` | Microsoft Defender: network protection | moderate |  | registry | |
| `security.passwordRevealOff` | No button to reveal passwords | safe |  | registry | |
| `security.llmnrOff` | LLMNR off | moderate |  | registry | |
| `security.autoRestartSignOnOff` | No automatic sign-in after restarts | safe |  | registry | |
| `security.remoteAssistanceOff` | Remote Assistance requests off | safe |  | registry | |
| `security.projectionToPcOff` | No wireless projection to this PC | safe |  | registry | |
| `security.autoPlayOff` | AutoPlay off on all drives | safe |  | registry | |
| `background.pcaOff` | Program Compatibility Assistant off | safe |  | registry | |
| `network.smbThrottlingOff` | Network file transfers: no SMB throttling | safe |  | registry | |
| `battery.noIndexingOnBattery` | No search indexing on battery | safe |  | registry | |
| `network.dohAutoUpgrade` | DNS over HTTPS for Cloudflare, Google and Quad9 | moderate, preview |  | dohAutoUpgrade | |
| `storage.reservedStorageOff` | Reserved storage off | moderate |  | reservedStorage | |
| `personalize.darkMode` | Dark mode for Windows and apps | safe |  | registry | |
| `personalize.accentTitleBars` | Accent color on title bars | safe |  | registry | |
| `personalize.startRecommendedOff` | Start without Recommended | safe |  | registry | |
| `explorer.hiddenFiles` | File Explorer: show hidden files | safe |  | registry | |
| `personalize.taskbarLeft` | Taskbar: icons on the left | safe |  | registry | |
| `personalize.clockSeconds` | Taskbar clock with seconds | safe |  | registry | |
| `personalize.taskbarNeverCombine` | Taskbar: never combine buttons | safe |  | registry | |
| `focus.taskbarFlashingOff` | Taskbar: no flashing buttons | safe |  | registry | |
| `focus.taskbarBadgesOff` | Taskbar: no badges on apps | safe |  | registry | |
| `personalize.showDesktopCornerOff` | Taskbar: no show desktop corner | safe |  | registry | |
| `personalize.snapAssistOff` | Snap: no suggestions for the other half | safe |  | registry | |
| `personalize.snapLayoutsOff` | Snap: no layouts on maximize button and screen top | safe |  | registry | |
| `explorer.compactView` | File Explorer: compact view | safe |  | registry | |
| `explorer.itemCheckboxes` | File Explorer: item check boxes | safe |  | registry | |
| `focus.notificationSoundsOff` | Notifications without sound | safe |  | registry | |
| `personalize.dynamicLightingOff` | Dynamic Lighting off | safe |  | registry | |
| `personalize.communicationsDuckingOff` | Game audio stays loud during voice calls | safe |  | registry | |
| `display.autoHdrOn` | Auto HDR on | safe, preview |  | registryToken | |
| `personalize.menuDelayShort` | Faster submenus | safe |  | systemParameter | |
| `personalize.hoverTimeShort` | Shorter hover time | safe |  | systemParameter | |
| `focus.startAccountNotificationsOff` | Start: no account notifications | safe |  | registry | |
| `focus.altTabTabsOff` | Alt+Tab: open windows only | safe |  | registry | |
| `privacy.searchHistoryOff` | Search: no history on this device | safe |  | registry | |
| `focus.lockScreenTipsOff` | Lock screen: no fun facts and tips | safe, preview |  | registry | |
| `focus.finishSetupOff` | No "Let's finish setting up your device" screen | safe |  | registry | |
| `explorer.syncProviderNotificationsOff` | File Explorer: no sync provider notifications | safe |  | registry | |
| `display.vrrOn` | Variable refresh rate for older full screen games | safe, preview |  | registryToken | |
| `gpu.gameDvrPolicyOff` | Game Bar recording off by policy | safe |  | registry | |
| `privacy.silentAppInstallsOff` | No automatic installs of suggested apps | safe, preview |  | registry | |
| `explorer.homeGalleryHidden` | File Explorer: no Home and Gallery | safe |  | registry | |
| `explorer.thisPcFolders` | File Explorer: user folders in This PC | safe |  | registry | |
| `gpu.gameBarControllerOff` | Xbox button does not open Game Bar | safe |  | registry | |
| `personalize.settingsHomeHidden` | Settings: no Home page | safe |  | registry | |

## 5. Tweaks that need real hardware

A VM has no NVIDIA GPU and only a synthetic network adapter. Test these on a spare real PC with the hardware named, one at a time, with a restore point first.

| Tweak | Title | Risk | Needs | Actions | Result |
|---|---|---|---|---|---|
| `nvidia.lowLatencyOn` | NVIDIA Low Latency Mode: On | safe, preview | NVIDIA GPU | nvidiaDrs | |
| `nvidia.shaderCacheUnlimited` | NVIDIA shader cache: unlimited | safe, preview | NVIDIA GPU | nvidiaDrs | |
| `nvidia.textureFilteringPerformance` | NVIDIA texture filtering: high performance | safe, preview | NVIDIA GPU | nvidiaDrs | |
| `nvidia.preferMaxPerformance` | NVIDIA power mode: prefer maximum performance (global) | moderate, preview | NVIDIA GPU | nvidiaDrs | |
| `network.nicPowerSavingOff` | Network adapter power saving off | moderate, preview | physical network adapter | nicProperty | |
| `network.nicAllowPowerOffOff` | Do not let Windows turn off the network adapter | moderate, preview | physical network adapter | nicProperty | |
| `network.interruptModerationOff` | Interrupt moderation off (Expert) | expert, preview | physical network adapter | nicProperty | |

### Undocumented values: check the effect

These tweaks use values Microsoft does not document and stay previews until a test shows that the value does what the page says. Check the visible effect after apply and after undo, then add the result to the tweak's `proof` in the catalog.

| Tweak | Title | Check |
|---|---|---|
| `network.throttlingIndex` | Network throttling off (NetworkThrottlingIndex) | the effect the explanation page describes |
| `display.autoHdrOn` | Auto HDR on | the effect the explanation page describes |
| `focus.lockScreenTipsOff` | Lock screen: no fun facts and tips | the effect the explanation page describes |
| `display.vrrOn` | Variable refresh rate for older full screen games | the effect the explanation page describes |
| `privacy.silentAppInstallsOff` | No automatic installs of suggested apps | the effect the explanation page describes |

<!-- End of generated tables. -->

## 6. Changes built at runtime and other features that change the PC

| Feature | Test | Result |
|---|---|---|
| Per-game NVIDIA profiles (real NVIDIA PC) | Create a profile for one game, check in NVIDIA Control Panel, undo | |
| MSI mode and interrupt affinity (Expert, real PC) | Change one device, restart, device works, undo, restart | |
| Startup entries | Disable one Run key entry and one Startup folder item, sign out and in: not started; enable again | |
| Services | Change one explained service to Manual, restart, undo | |
| Scheduled tasks | Turn one task off and on again | |
| Command line recommended and export | `--apply-recommended --profile battery` on a desktop: exit 9 and the profiles this PC offers; `--apply-recommended --profile gaming --log r.txt`: exit 0, the profile and each change in the log; `--export x.json`: exit 0 and the applied ids in the file, again to the same path: exit 2 (never overwritten); `--undo-all`, then `--apply x.json` and `--undo-all` again: everything comes back and goes again | OK 2026-10-13 (26H2: three changes applied, exported, undone, applied from the file, undone) |
| Copy to new accounts | Apply four user tweaks (`explorer.fileExtensions`, `personalize.taskbarLeft`, `privacy.webSearchOff`, `explorer.classicContextMenu`); Copy to new accounts: the confirmation lists only Tunero's values under `C:\Users\Default\NTUSER.DAT` (not the classic menu, which lives in UsrClass.dat), no `TuneroDefaultProfile-*` stays under HKU; a new local account started with its profile sees the values; `--undo-all` puts the Default profile back | OK 2026-10-13 (26H2): HideFileExt 0, TaskbarAl 0, DisableSearchBoxSuggestions 1 in the Default profile and in the new account, the classic menu left out, after undo all three gone again. Found and fixed: the hive stayed loaded while the restore point was made, and an app ended at that moment left it loaded (now loaded only after the restore point, leftovers unloaded at start); Game Mode and Storage Sense, on only because Windows' default matches, were copied too (now only tweaks with a Tunero backup) |
| Export parts | Remove one reviewed app, set one explained service to Manual and turn off one Run entry; tick the three parts under "Also export" and export: the file has `apps`, `services` and `startup`. Undo all (the app stays removed), add a reviewed app that is still installed to `apps` by hand, then Import the file: the service and the Run entry change again, then a question names the added app; No keeps it. `--export y.json --include apps,drivers`: exit 10 and no file; `--apply` of the file: the log names the app and does not remove it | OK 2026-10-13 (26H2): Import of a hand-made file set MapsBroker to Manual, turned off the Run entry and, after its own question, removed News; the export with the three boxes had `apps`, `services` and `startup`, the same with `--include`; `apps,drivers`: exit 10, no file; `--apply` named Weather in the log and left it installed; `--undo-all` put MapsBroker back to Automatic |
| Re-apply task | Copy the exe to `C:\Program Files\Tunero`, start it there: Settings offers "Apply my changes again"; turn it on (the confirmation names the task), `Get-ScheduledTask -TaskPath \Tunero\` lists "Apply changes again <SID>" with a logon trigger, highest privileges and `--reapply`. Apply "Advertising ID off", delete its value by hand, `Start-ScheduledTask`: nothing comes back (no update since). Set `appliedOnVersion` in its backup file to an older build, start the task again: the value is back and the change log says so. Turn the switch off: the task is gone. Turn it on again, then Settings > Uninstall: the task and the `\Tunero` folder are gone. From Downloads the switch is off with the reason | OK 2026-10-14 (26H2, from Program Files, the window driven through UI Automation): the switch returned at once and the confirmation opened; on registered "Apply changes again <SID>" (logon trigger, PT2M, highest privileges, `--reapply`), off removed it, on again registered it. Advertising ID off applied, its value deleted, the task started: nothing came back (no update since); with `AppliedOnVersion` set to 26100.1 the next run applied it again ("Applied again: 1") and recorded the current version. Uninstall (keep changes) removed the task, the \Tunero folder, the data folder and the exe. Earlier run OK 2026-10-13 (before the change to updates only, one task per account and removal on uninstall): OK 2026-10-13 (26H2, from Program Files): switch on with its confirmation, task with logon trigger, PT2M, highest privileges and --reapply; MouseSpeed set back by hand returned when the task was started by hand ("Applied again: 1"); Undo all removed the task. Found and fixed: registering into the missing \Tunero folder failed |
| Startup Delete entry | Add a Run value pointing to a missing file, a RunOnce value and a cmd.exe AutoRun by hand; Delete entry on each: the confirmation shows the value, the entries leave the list, the Changes page lists three deletions, and Undo writes each back with its type (an expandable string stays one) | OK 2026-10-13 (26H2: Run, RunOnce and AutoRun deleted, three rows on Changes, Undo all wrote each back, the Run value as an expandable string again) |
| Unused program folders | Create `C:\Program Files\Old Tool` with a file dated a year back (`(Get-Item ...).LastWriteTime`), and keep 7-Zip (MSI) installed: "Find unused program folders" lists Old Tool unticked with size and date, never 7-Zip, Common Files, Windows' own folders or a folder of a running program; Move to Recycle Bin after ticking it | OK 2026-10-13 (26H2: only the year-old Old Tool listed, unticked, with size and date; 7-Zip, Steam, Common Files and Windows' folders not listed) |
| Search winget | Install & update: search "notepad": up to 50 results with name, id and version; "a" or a query with quotes is refused with a note. Install a machine package from the results (Notepad++): the confirmation says it is not reviewed, winget's lines show under the results, "Installed". Install a package with only a per-user installer: winget opens for the signed-in user, not elevated | OK 2026-10-13 (26H2: "a" refused with the note; "notepad" found 35 with id and version; Notepad3 installed for the whole PC after the confirmation. The per-user fallback is not run yet) |
| Startup Delete for files and tasks | Put a shortcut into the user's Startup folder and register a daily task of another program (and one with "Run whether user is logged on or not" and a stored password); Delete entry on the shortcut and the first task: both leave the list, the Changes page lists them, Undo writes the shortcut back byte for byte and registers the task again with its trigger and account; the task with a stored password shows no Delete | OK 2026-10-13 (26H2: a Startup folder shortcut and a SYSTEM task with a daily trigger deleted; Undo all wrote the shortcut back byte for byte and registered the task again with its account, trigger and command. The stored-password case is covered by tests only) |
| Startup page tasks | Startup lists a third-party task with a daily trigger; Windows' own tasks (TextServicesFramework\MsCtfMonitor) show "Listed only" and no switch; a task that runs a COM handler shows its DLL and "Microsoft" | OK 2026-10-13 (26H2: a daily task of another program has a switch; MsCtfMonitor shows "Listed only", MsCtfMonitor.dll and "Microsoft Windows, Signed") |
| Windows features | Turn one optional feature on, restart, turn it off again | |
| Remove apps | Remove two inbox apps, check they are gone for a new user too, reinstall from the Store link | |
| Live readings: programs per drive | Elevated, readings on: copy a 2 GB file to a second disk (`Copy-Item` and Explorer): that disk's card names the copying program (Explorer or PowerShell), not System; save a large file from Notepad: Notepad, once Windows writes it; `logman query -ets` lists `Tunero disk activity` while the page shows and not after leaving it or switching off; end Tunero in Task Manager with the readings on, start it again: the readings work (the leftover session is stopped) | Probe in PCO-Test 2026-10-19 (26300): a 500 MB `File.Copy` went 481 MB to the copying program and 4 MB to System; the session was gone after closing and a crash's leftover was stopped on the next start; ~250 000 small cached reads per second cost 0.9 % of the processor, no lost buffers. The app itself not run yet |
| Live readings: details and End task | Select Notepad on the processor card: Open in Explorer selects notepad.exe; Add a priority rule opens Tools on the priority tab with notepad.exe filled in; End task asks, then Notepad closes; lsass and Memory Compression show the reason instead of End task, a svchost points to the Services page, MsMpEng (protected) says Windows does not let Tunero end it and offers no priority rule; ending Chrome (several processes) reports it ended; leave Health and come back: processor Since on does not jump; the Debug build (not elevated) shows "Programs per drive are not available" on drive cards | |
| OneDrive | With Known Folder Move on: uninstall is blocked; without: uninstall works | |
| Cleanup | Each of the 12 categories: shown size matches, files are gone afterwards, files in use are skipped, only the signed-in user's Recycle Bin is emptied | |
| Storage page | Opening the page reads nothing but the drive list (no banner). Analyze C: with Stop in the banner: Stop during the scan keeps the result before; a full run shows the result line (folder by folder, without administrator rights, it ends with how many folders could not be read); Folders shows the tree beside the treemap, and a tile selects its row; File types lists the groups, opening one lists its extensions, selecting one lists its largest files; Largest files fills when its tab opens; Find duplicates lists groups of identical files | |
| Storage: reader | A run without administrator rights (the Debug exe) reads folder by folder (the result line says so); the elevated exe reads C: from the file table (the result line says so) | Partly OK 2026-10-14 (26H2, over PowerShell Direct, UI not seen): the Debug exe started with the account's filtered token (`Start-Process -Credential`) and `--storage-compare C:` says "Elevated: False", "Analyze reads it folder by folder", and the file table refused with error 5 (access denied); the published exe in the elevated session says "Analyze reads it from the file table". The result line on the page is still to check |
| Storage: file table and folder by folder agree | `--storage-compare C: --storage-out <file>` with the published exe in an elevated session. The totals differ by design: the file table counts a hard-linked file once, the folder walk once per name (count the extra names read only with GetFileInformationByHandle on every file: names of files with more than one link minus their distinct file indexes), and folders the walk cannot open (System Volume Information) are only in the file table. Beyond that they agree within the size of files that changed in between. Then time and memory: each reader on its own (`--storage-reader FileTable`, `Folders`) seven times in a row, the first run not counted; the file table needs at most half the time of the folder walk and less memory | OK 2026-10-14 (26H2, 78.9 GB C: with an MFT of 550 MB): file table 59.56 GB (10^9 bytes), 177,871 files, 52,499 folders, 0 lost; folder by folder 69.18 GB, 237,255 files, 52,477 folders, 21 folders it could not open. Extra hard link names counted separately: 59,478 names with 13.85 GB, against a difference of 59,384 files and 9.62 GB plus the 4.2 GB in System Volume Information that only the file table sees; the rest lies in the folders the walk cannot open or was written between the runs. Time and memory, medians of six warm runs each, every record of the file table read: file table 0.59 s (0.35 s of it reading all 550 MB), folder by folder 1.21 s, so 0.49 of the time; managed heap peak 65 MB against 86 MB, working set peak 68 MB against 92 MB above the start. The first run of each series was slower (file table 0.65 s, folder walk 1.94 s right after the file table's runs). Without administrator rights: 47.11 GB, 204,767 files, 112 folders it could not open |
| Storage: OneDrive | OneDrive Files On-Demand folders show their sizes and analyzing them downloads nothing: files that were online-only before are still online-only afterwards (Status column in File Explorer, free space unchanged). Find duplicates, with the file table reader (elevated exe) and with the folder walk (Debug exe), downloads nothing either: two online-only copies of one file of 1 MB or more stay online-only and are not listed. Select a OneDrive folder, a file in it (online-only and always kept on this device) and a file in a subfolder, in the tree and in Largest files: the command bar says "In OneDrive or another cloud folder: remove it in File Explorer" and Move to Recycle Bin stays off. A file symbolic link (`mklink`) says "A link" the same way. Analyze a folder inside OneDrive (`--storage-scan`): every file and folder in it says the same before anything is moved | |
| Storage: Recycle | Select a folder of test files on the Folders tab and Move to Recycle Bin: the confirmation lists it with its size, it leaves the tree, the map and the lists, and Restore in the Recycle Bin puts it back with its files. Tick several files on Largest files: the same. A file held open by another program stays in the list and the result names it. Tick every copy of a duplicate group: the command bar says to keep one and Move to Recycle Bin stays off. With a separate admin account the confirmation says the items go to that account's Recycle Bin | |
| Storage: refused | `C:\Windows`, `C:\Program Files`, a Steam library, `C:\Users\<you>\AppData`, the profile folder `C:\Users\<you>`, `C:\Users`, Documents, a folder that holds a game library, `pagefile.sys`, a system file, `C:\Config.Msi`, `C:\Windows.old` (after a feature update), and a folder `data.` made with `mkdir "\\?\C:\Test\data."` next to `C:\Test\data`: the command bar gives the reason and Move to Recycle Bin stays off; a file or folder inside Documents can be moved | |
| Storage: Open in Explorer | Open in Explorer on a file and on a folder: File Explorer opens with the item selected and runs as the signed-in user, not elevated (Task Manager, Details, the Elevated column says No); the same with a separate admin account | |
| Apps | Install one per-user app (runs as the signed-in user) and one machine-wide app with winget | |
| DNS presets | Choose Cloudflare in the DNS servers list, check `Get-DnsClientServerAddress`; choose Google: Cloudflare is undone first (its own confirmation), then Google is applied; choose Automatic: DHCP or the old servers are back; cancel a dialog: the list shows the servers in use again | OK on 26H2 Pro, 2026-10-13, through `--apply` and `--undo-all` (Cloudflare set, then DHCP again; the change through WMI with time limits) |
| DNS benchmark | Runs, shows results, changes nothing | |
| Tools | Winsock reset (restart needed), Explorer restart, DNS flush, Windows Update repair, SFC and DISM with live output | |
| Health | Frame time benchmark with PresentMon; throttle check; PawnIO install from the Health page and uninstall with winget | |
| Desktop program uninstaller | Uninstall one machine-wide program (restore point first, runs elevated) and one per-user program (runs as the signed-in user); an MSI entry under HKCU runs msiexec as the user; the list updates after Refresh | |
| Uninstall several programs | Tick an MSI program (7-Zip MSI), an Inno Setup program with a QuietUninstallString and a per-user program; Uninstall selected with "Quietly where possible": the confirmation names which run quietly, the MSI and Inno ones run without a window one after the other, the per-user one shows its window and the queue waits until it has left the list (Continue skips the wait, Stop starts no more). Afterwards "Left behind" lists a folder the uninstaller kept (create a file in the program folder before) and its Start menu shortcut, never Program Files itself or a shared vendor folder another installed program uses; Move to Recycle Bin puts them there and they can be restored | OK 2026-10-13 (26H2: 7-Zip MSI, Inno Setup and VS Code for the user removed quietly in a row, the queue waited until VS Code left the list; three folders and three shortcuts offered and moved to the Recycle Bin with their original places. The run found two bugs, fixed the same day: VS Code removes its entry before its files, so the check now waits until the folders stop changing; 7-Zip's MSI records no folder, which now comes from Windows Installer's component records) |
| Update all (winget) | Runs `winget upgrade --all --scope machine` with the output in the app, then a winget window as the signed-in user for per-user programs; a winget copy of Tunero is not replaced while it runs | OK 2026-10-13 (26H2): 7-Zip 23.01 to 26.04 machine-wide with the output in the app, then a winget window as the signed-in user (not elevated) updated App Installer and Windows Terminal; the winget copy of Tunero not tested (none installed) |
| Update all with Chocolatey, Scoop and the Store | PCO-Test with Chocolatey (its installer, `C:\ProgramData\chocolatey`) and Scoop (as the signed-in user) installed, each with one outdated package (`choco install git --version <older>`, `scoop install 7zip@<older>`): the Package managers card lists winget, Chocolatey, Scoop and the Microsoft Store as included, with versions. Update all: one question names all four; `choco upgrade all` runs elevated with its output in the card; Scoop's window runs `scoop update`, then `scoop update *`, as the signed-in user (Task Manager: not elevated) and waits for Enter; the Store opens on its updates page. Check Chocolatey updates lists the outdated package with both versions, and its Update runs `choco upgrade <id>`. Then give Users write access to `C:\ProgramData\chocolatey`, and in a second run only to its `helpers`, `extensions` or `lib` folder (icacls): each time the card says Chocolatey is not included and nothing runs it elevated. Every choco command line Tunero starts ends with `--ignore-http-cache` (Process Monitor or the log); with Chocolatey 2.0.x installed (`choco upgrade chocolatey --version 2.0.0 --allow-downgrade`), the card says the version is too old and Chocolatey is not included, and Update now of a Chocolatey copy of Tunero shows the command. Check Chocolatey updates with the network cut off: within 2 minutes the card says Chocolatey could not check or did not answer, and the page works again. Developer tools (Node.js, Python, a .NET global tool) are listed with Copy only; nothing starts from a user folder elevated (Process Monitor, Process Start events of Tunero.exe) | |
| .NET Desktop Runtimes | Install .NET Desktop Runtime 8 and 10 from the Apps page | |
| Fixes and repair (Tools) | Each of the 10 quick fixes: renew IP (connection back afterwards, also when one adapter fails to release), TCP/IP reset (restart needed), audio, Bluetooth and Search restart, graphics driver restart (screen flashes, apps keep running), time sync, performance counters, chkdsk /scan (output shown), recovery environment on | |
| New quick fixes (Tools) | Rebuild the icon cache: Explorer ends, `%LocalAppData%\IconCache.db` of the signed-in user is gone and comes back, Explorer runs again unelevated. Update Microsoft Defender: Windows Security shows a new "last update" time. Reset winget sources: add a test source first (`winget source add`), after the reset only msstore, winget and winget-font are listed and Install & update still searches. Restart to Advanced startup: the PC restarts into the blue menu, Startup Settings > 4 starts Safe Mode, and the next restart is normal (no `safeboot` in `bcdedit`) | OK 2026-10-13 (26H2: icon cache deleted and Explorer back as the signed-in user; Defender update ran (definitions were current, so the message now says it has the newest); winget source reset ran and a search worked afterwards. Advanced startup, driven through the Hyper-V keyboard and screen: the VM restarted into Choose an option, Troubleshoot > Advanced options > Startup Settings > Restart > 4 gave the Safe Mode desktop, and the restart after it was a normal boot without `safeboot`. Found and fixed: the confirmation's button said "Run" for a restart, and every quick fix button was named only "Run" or "Restart" for screen readers) |
| Program start priorities | Add a rule for PING.EXE with "Below normal" and low disk priority (notepad.exe on Windows 11 is only a stub for the Store app), start `ping -n 6 127.0.0.1`: Task Manager shows the priority; remove the rule: back to normal | OK 2026-10-13, build 26300: BelowNormal and I/O priority 1 with the rule, Normal and 2 after Undo, PerfOptions key removed. Memory priority: PagePriority 1, 2 and 3 gave memory priority 1, 2 and 3 (GetProcessInformation), 5 again without the value |
| Windows capabilities | Remove WordPad or the Steps Recorder and add it back (needs Windows Update) | |
| Reserved storage | Turn off and on again with DISM; while an update uses the space, the change is refused with DISM's message | |
| Start menu entry | First start of the published exe: the question appears once; "Add to Start menu" creates `Tunero PC Optimizer.lnk` in the signed-in user's Start menu and Windows Search finds it as "Tunero" and as "optimizer"; an entry `Tunero.lnk` from an earlier version that starts the same exe gets the new name on the next start; the switch in Settings removes and adds it; the question does not come back on the next start | OK 2026-10-13 (26H2): rename of `Tunero.lnk` and Windows Search for "optimizer" |
| winget install and update | `winget install --manifest <folder>` with the manifest of the previous release (LocalManifestFiles on), start with `tunero`, Update now: the app closes, a PowerShell window runs `winget upgrade`, the app starts again after the UAC prompt and shows the new version; `winget list jozuern.Tunero` shows it too. Before the new version is in winget: the window says so and waits for Enter, then the old version starts | |
| Chocolatey uninstall removes the re-apply task | `chocolateyUninstall.ps1` from `scripts/chocolatey-package.ps1`, with two tasks in `\Tunero\`: one starting the package's `Tunero.exe`, one starting `C:\Program Files\Tunero\Tunero.exe`. Only the first is removed | OK 2026-10-19 (26H2 Pro, dummy tasks registered as SYSTEM; the Program Files task stayed) |
| Chocolatey install, update and uninstall | `choco install tunero --source <folder with the nupkg>` (package from `scripts/chocolatey-package.ps1`), start with `tunero` from a non-elevated terminal (UAC prompt, the terminal is not held); Update now runs `choco upgrade tunero` in an elevated window and starts the app again; Settings > Uninstall runs `choco uninstall tunero`, the shim and `lib\tunero` are gone, and so is the data folder. With `choco.exe` in a folder users can change, the app shows the command instead | OK 2026-10-12 (26H2, local package served on localhost) |
| Scoop install, update and uninstall | `scoop install <path>\tunero.json` (manifest from `scripts/scoop-manifest.ps1`): Scoop Apps\Tunero in the Start menu, the app offers no own entry; Update now runs `scoop update tunero` as the signed-in user and starts `current\Tunero.exe`; Settings > Uninstall runs `scoop uninstall tunero` and removes the data folder. A `--global` install shows the command instead | OK 2026-10-12 (26H2, local package served on localhost) |
| Startup snapshot | Save a snapshot, install a program that adds a Run entry, compare: only that entry is "new"; save while the list loads is refused | |
| DNS "Use the fastest" | After the benchmark, the fastest preset is applied; with another preset of this app applied, that one is undone first | |
| Edge Game Assist | Listed on Remove apps when installed; removing it keeps Edge | |
| Data folder | Create `C:\ProgramData\Tunero` as a standard user with a file in it before the first start: the app deletes the folder and creates a locked one | |
| Two Windows accounts | Account A applies dark mode, account B starts the app: B sees dark mode as not applied by the app and can apply and undo it without touching A's backup | |
| Upgrade from PCOptimizer (rename) | Version 0.7.3 applies two tweaks (file extensions and the gaming power plan, which it names "PCOptimizer Gaming"); then `Tunero.exe` starts: `C:\ProgramData\PCOptimizer` is moved to `C:\ProgramData\Tunero`, the Changes page lists both, and `--undo-all` undoes them, deletes "PCOptimizer Gaming" and restores Balanced | OK 2026-10-12 (26H2, 0.7.3 code from the v0.7.3 tag) |
| Uninstall (downloaded exe) | Apply two tweaks and add the Start menu entry; Settings > Uninstall > Undo changes and uninstall: both tweaks are undone, the app closes, and within a minute the exe (and a `.old` from an update), `C:\ProgramData\Tunero` and `Tunero PC Optimizer.lnk` are gone. Again with Keep changes and uninstall: the tweaks stay applied. With a change that cannot be undone (registry key locked by hand), nothing is removed and the Changes page lists it | OK 2026-10-12 (26H2) |
| Uninstall (winget copy) | Installed with `winget install`: Settings > Uninstall opens a PowerShell window as the signed-in user that runs `winget uninstall` after the app has closed; `winget list jozuern.Tunero` finds nothing and the data folder is gone | OK 2026-10-12 (26H2, installed from a local manifest) |

## 7. Windows install media

Build the ISOs in PCO-Test with the published exe: `Tunero.exe --build-media <request.json> --log <file>` in the elevated PowerShell Direct session builds from a request file with the page's own mapping of choices (format in CONTRIBUTING, developer switches); build at least one ISO through the page itself (`--screenshot` cannot drive a build: run the exe through a scheduled task while the owner is signed in on the console). Install each ISO in a new throwaway VM, never in PCO-Test itself; delete the throwaway VMs afterwards. Every installed VM gets the health check below, and each result goes into its row with the date and build.

| Test | Expected | Result |
|---|---|---|
| Health check (every installed VM) | One script, run through PowerShell Direct: `dism /Online /Cleanup-Image /ScanHealth` reports no corruption; `sfc /verifyonly` finds no integrity violations; `reagentc /info` shows Windows RE enabled; Start menu, Settings, File Explorer, Notepad, Snipping Tool, Terminal and the Microsoft Store start (process alive after 10 s, started through a scheduled task in the signed-in session); no Application or System log entries of level Error from Windows components during setup that the untouched official ISO (VM 2) does not also produce; on the Default Switch, Windows Update finds and installs the newest cumulative update and the VM restarts into it | |
| VM 1: every change | Download source, all four changes with every allowlisted app ticked, a Gen2 VM with virtual TPM and Secure Boot. Setup asks only for the drive; the setup questions are skipped; the local account signs in on its own. Before the desktop nothing of Tunero shows; then its window opens with "Setting up this PC with the <profile> profile" in the banner (no setup screen, no Start menu question, no confirmation, no restore point question, no Stop), and Changes opens with the result line (applied, failed, not applied; no restore point made). Changes lists every applied change, and Undo works on one of them. `C:\Program Files\Tunero\Tunero.exe` exists, Installed apps lists Tunero, the Start menu has its entry, `Get-ComputerRestorePoint` lists none made by Tunero, and `C:\Windows\Setup\Tunero` is gone. The chosen apps are absent (`Get-AppxProvisionedPackage -Online`). Record `net user <name>` ("Password expires" and "User must change password"). Sign out and in again: Windows asks for a new password. Health check passes | |
| VM 1, first sign-in edge cases | Install again from an ISO whose `first-sign-in.json` names `laptopGaming` (on the desktop VM): the window says the profile is not available and nothing is applied, Changes is empty. An ISO built by a Debug or unsigned Release exe: the exe is not installed in Program Files; it runs from `C:\Windows\Setup\Tunero`, applies the profile, and the folder stays (the log says why). An ISO whose `first-sign-in.json` was made invalid by hand: Windows reaches the desktop, no Tunero window, the log says nothing was done | |
| VM 1, first sign-in language, theme and timing | Two ISOs from the VM 1 settings, one with English and Light, one with German and Dark: at the first sign-in the window opens in that language and theme, and later starts keep them. FirstLogonCommands returns within seconds (the desktop does not wait for the apply; compare the time of the `--first-sign-in` log lines with Explorer's start in the event log) | |
| VM 1, offline first sign-in | The VM 1 ISO with the VM's network adapter disconnected until the desktop shows: record whether the signature of the copied exe was trusted offline (Tunero installed in Program Files, log "installed, starting the installed copy") or the setup-folder fallback ran (log "setting up this PC from the setup folder", the folder stays); either way the profile is applied once and Windows reaches the desktop | |
| VM 1, runs once | After VM 1's first sign-in: sign out and in again, nothing is applied again (Changes unchanged, the log says no pending first sign-in); `"C:\Program Files\Tunero\Tunero.exe" --first-sign-in-apply gaming` opens the window normally and applies nothing; copying the setup folder back and running `C:\Windows\Setup\Tunero\Tunero.exe --first-sign-in` does nothing (log: set up already). `C:\ProgramData\Tunero\first-sign-in.done` exists, `first-sign-in.pending` does not | |
| VM 1, newer installed copy kept | Install a newer Tunero into `C:\Program Files\Tunero` (Settings > Install, or a build with a higher version), delete `first-sign-in.done`, put the older media copy and its `first-sign-in.json` into `C:\Windows\Setup\Tunero` and run `--first-sign-in` there (elevated): the newer exe stays (its version unchanged), it starts with the profile and applies it, and the setup folder is removed; the log says the newer copy was kept and the setup copy is removed with its folder | |
| VM 1b: BIOS boot | The VM 1 ISO in a Gen1 VM (BIOS): installs, health check passes | |
| VM 1c: defaults | The default choices (consumer apps only, setup skipped, Gaming profile) in a Gen2 VM: installs, Tunero applies Gaming, health check passes | |
| VM 1d: 2023 Secure Boot certificate only | If Hyper-V offers such a template or the VM's Secure Boot database can be set up that way: the ISO boots. If no VM can model it, say so here | |
| VM 1e: in-place upgrade | In an installed VM, mount the ISO and run `setup.exe` (keep files and apps): the upgrade finishes, files and apps stay, no second account appears, and the answer file causes no prompt or wipe | |
| VM 1f: Reset this PC | Reset this PC (keep my files) on VM 1: finishes and Windows starts | |
| VM 2: official ISO, no changes | Own ISO as source (Microsoft's, untouched), every change off: installs like the original ISO; it is the baseline for the health check's error log comparison | |
| VM 3: unsupported PCs | Gen2 VM without TPM and with 2 GB memory: the ISO with "Allow unsupported PCs" installs; the same build without the option is refused by Setup ("This PC can't run Windows 11") | |
| VM 4: Stop and a killed Tunero | Stop in each stage of a build (catalog, download, check, unpack, apps, files, ISO, verify), and once end Tunero in Task Manager while the image is mounted (apps stage): after the next start of Tunero, `dism /Get-MountedImageInfo` lists no image of Tunero and no `TuneroMedia.*` folder is left at any drive root | |
| Publishing the ISO | The ISO opens in Explorer at the output path (mounts, files readable); an output folder at a drive root (`D:\`) works; `icacls` of the published ISO shows the output folder's inherited permissions (the signed-in user can open, move and delete it), not the work folder's administrators-only ones | |
