# Changelog

## Unreleased

### Changed

- **.NET runtime 10.0.14:** the exe carries the .NET and Windows Desktop runtime of Microsoft's out-of-band security release of October 25, 2026, which fixes a Brotli decoder flaw ([release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md)), instead of 10.0.13.
- **Consistent pages:** empty lists say so the same way on every page, switches for settings show On or Off everywhere (also on Remove apps and in the install media's Expert options), and cards share one padding.
- **Restore point question:** shorter buttons ("Turn on", "Continue without it") on one line at one height; dialog buttons always share their height.
- **Details pane:** text keeps clear of the scroll bar, the impact shows its number once, registry paths keep the line spacing, and the "already set" note reads clearly.
- **Changes:** the Move to another PC card has its buttons at the top right and the export options in a row below. **Startup** names the total once ("Showing 4 of 51. 16 are turned off."), and German reload buttons say "Neu laden" on every page.
- **Navigation** group headings show whenever they fit, also after the start-up scan; a running scan takes the "Last scan" line instead of adding one.
- **Windows install media:** the summary bar breaks only between items, the failure bar holds its Show why button, and the review names the keyboard layout.
- **Remove apps:** note and info bars first, then the Remove selected card above a divider; **Graphics & DNS** has a Graphics section header; **Storage** treemap labels sit on the tiles in black or white.
- **Windows install media:** a failed build scrolls to its reason, and the bottom bar says the last build failed. The bar shows the image and the changes on two lines, names Allow unsupported PCs, unreviewed apps and apps to install, and gives the real reason Build is off. Card headers are shorter, with the details in the expanded cards.
- **Drivers:** each driver's age lines up on the right of its card; the DDU card moved below the list.
- **Tools:** Restart to Advanced startup says it restarts the PC now; start priority rules show their tags, the warning in an info bar, a labeled form and an empty state; the German tab is Reparaturen.
- **Install & update:** the reload button is Neu laden in German, so it no longer reads like an update.
- **Graphics & DNS:** without an NVIDIA card, a card says why there are no driver settings; the network links moved to Related settings at the end of the page.
- **Remove apps:** preinstalled apps under a header per group, a bar that always shows Remove selected, and OneDrive and Edge in one section.
- **Storage:** before an analysis the page explains Analyze; the folder list is wider than the treemap, larger tiles carry names, and the legend lists only the types on the map.
- **Cleanup** sizes read "157 MB in 1,566 files".
- **System info:** unnamed displays show as Built-in display or Display 2; the processor card says L3 cache and names its core types; its Technical details switch is the one from Settings.
- **Settings** switches show On and Off; **Health** duration boxes have labels; **Changes** keeps the extra export options in the Move to another PC card.
- **Overview:** the verdict names settings that work against the profile instead of saying nothing holds the PC back, and the readiness line counts unknown checks.
- **Tweaks:** every group opens when the list has 15 tweaks or fewer; a tweak that works against the profile says why under its title.
- **Navigation:** group headings show whenever they fit, thin lines between groups when they do not.
- **Wording:** profile and drift banners, the works-against hint, the Expert link, the Modern Standby block, the ignore button, the Manual checks summary; search results no longer repeat the title.
- **Startup:** "Show Windows entries" (off by default) replaces "Hide Windows entries", as on Services & tasks, and also hides Windows tasks, Known DLLs and Active Setup stubs whose file is gone. Service switches say Automatic or Manual, the start types they set. The second tab is Other scheduled tasks, and the summary starts with the count the tab shows.
- **Services & tasks:** task commands show once, on one line and only with Technical details; Windows tasks show "Listed only" instead of a grey switch; notes no longer repeat the service's name.
- **Ratings follow the evidence:** the power mode check is rated 1 on desktops (3 on laptops), the Balanced plan rates for gaming only when coming from Power saver, and HAGS on RTX 40 and 50 no longer says it is needed to start games.
- **Inking and typing personalization off** is rated Moderate and says up front that it deletes the personal dictionary, which Undo does not bring back.
- **Markdig 1.4.2** for the explanation pages.

### Fixed

- **Startup:** Windows' leftover shell service object WebCheck counts as a Windows entry; **Services & tasks** notes no longer repeat the name when only hyphens or spaces differ; the priority form's check boxes line up.
- **Ignore:** a result ignored while a scan runs no longer leaves a change recommended for it until the next scan; the note that all changes survived a Windows update follows a language switch.
- **German:** a few sentences that could read as formal address now say what they mean (the storage protection notice, three explanation pages).
- **Windows install media:** Build and Resume stay off while another change or an update runs, and a saved build is no longer deleted when a build could not start. A partly ticked app group ticks the whole group on click; choices loaded while your ISO is read keep their edition; German text uses "du" throughout.
- **Install & update:** the app list follows installs and removals since the last scan; one unreadable uninstall entry no longer hides the whole program list; Signal no longer shows as installed when only SignalRGB is; Update all says when winget cannot open its window for per-user programs.
- **Rebuild the icon cache** also deletes the `iconcache_*.db` files Windows 10 and 11 use.
- **Benchmark:** a stopped or timed-out capture no longer leaves its CSV file behind.
- **Graphics & DNS:** links and benchmark columns are no longer cut off in narrow windows; switching DNS servers no longer waits for an extra full scan.
- **Remove apps:** the Left behind card scrolls on short windows, and a page opened during the first scan loads once.
- **Cleanup:** Temporary files keeps empty folders created in the last day, which a running installer may still need.
- **Storage:** with a separate administrator account, the signed-in user's own Documents, Desktop and other personal folders stay protected, also when moved to another drive; picking a file type again no longer lists each file twice.
- **System info:** HDR shows as on only when Windows runs the display in HDR, not for Auto Color Management on an SDR display.
- **Live readings:** a svchost's name and services follow what it runs now; two Tunero copies in different sessions no longer stop each other's disk trace, and a trace that stops on its own says so.
- **Setup screen** says why Install is refused while other work runs; the VirusTotal Save button waits for a key.
- **Wording:** counts read right for one item, the restore point dialog says "Continue without a restore point", and the uninstall dialog explains that the PawnIO driver can stay.
- **Details and search** always open the tweak, also when filters, a not-applicable state or Expert mode would hide it.
- **Dialogs and the window** fit the monitor they open on, not the primary one; a confirmation with nothing to apply only offers Close.
- **Show in Explorer** says so when the file is gone or File Explorer did not start.
- **Startup and Services:** services and drivers from driver packages show their real names instead of `@oem19.inf,%...%;` strings.
- **Startup:** Startup folder files, shortcut targets, Explorer extensions and service DLLs are checked as the file itself, so a planted program in a folder with a space cannot pass as signed; rundll32 lines with a quoted DLL are flagged when they start something else. The CSV export names every signature and the current switch state.
- **SysMain** is changed only by its tweak, so the Services page and the tweak never keep two different originals.
- **Memory speed advice** recognizes Z590 and micro-ATX Z boards (Z390M and similar), so their 8th to 10th gen Intel PCs get the XMP advice again; memory module mix advice no longer shows in virtual machines.
- **NVIDIA global settings:** the G-SYNC frame cap follows the G-SYNC display's refresh rate, not a faster second monitor.
- **Explanation pages:** the recovery command for the platform clock, the editions of the web search policy, current page names and German wording.
- **`--apply-recommended`** picks the profile as the window does: a saved profile this PC does not offer (a laptop profile on a desktop) falls back to Gaming, as on the Overview.
- **Conflicts:** two changes that write the same setting (a registry value, an NVIDIA driver setting, one side of a power setting) now conflict even without a declared conflict, so batch applies, Undo all and the "changes reset" notice stay right.
- **NVIDIA global settings:** with G-SYNC and V-Sync forced on, the quick fix sets the frame rate limit 3 FPS below the refresh rate instead of removing it.
- **Apply again before a restart** no longer reports a failure when another program holds the undo record; the change is in place.
- **Command line:** a sign-out or shutdown during `--apply`, `--undo-all`, the sign-in reapply task or `--build-media` no longer ends it halfway; Windows waits, as for changes made in the window.
- **Install media:** an app that installs at the first sign-in no longer reports winget 0x00041303 while its one-time task still waits to start.
- **Two copies:** a copy under another file name, such as `Tunero (1).exe`, no longer unloads the Default profile or install media hives of a copy that still runs.
- **Undo:** a registry key that Tunero could delete only in part (a subkey Windows protects) comes back in full on Undo. Before, Undo took what was left for a key Windows had made again, reported success and did not restore the deleted values.
- **Uninstall leftovers:** Move to Recycle Bin asks first, and checks each folder again right before it moves it, with the same evidence as Find unused program folders (autostart entries, services, scheduled tasks, running programs, taskbar pins). A folder something uses by now, such as a program installed again into it, stays and says why.
- **Storage:** a file or folder right below a drive root (such as `D:\Old backups`) moves to the Recycle Bin. Before, the move always ended as "not moved".
- **Scan:** the check for leftovers of other tools finds the forced platform timer on German Windows too, where bcdedit prints "Ja".
- **Screen readers:** the result of a change, a cleanup or another task is read out also when the result bar had closed by itself before.
- **Remove apps:** the warning for an app that is not on the reviewed list says that its data in every account goes with it (for a Linux distribution of WSL: all its files).
- **Startup:** the CSV export uses the separator Excel expects for your regional settings, also when the app language differs from them (an English app on German regional settings).
- **Apply my changes again:** a change you turned back in Windows yourself, after a Windows update had left it in place, stays turned back. Before, the task applied it again at every sign-in and the Changes page blamed the update.
- **DNS servers: automatic** leaves network adapters with a fixed IP address alone. Before, it cleared their DNS servers, and no router hands them new ones.
- **Uninstall leftovers:** a folder another installed program still uses is not offered when its uninstall entry or service names it with an 8.3 short name (`C:\PROGRA~2\...`).
- **Update in the app:** works for a Tunero.exe in a drive root, such as a USB stick. A failed update no longer leaves the downloaded copy (`Tunero.exe.new`) beside the exe.
- **Services & tasks:** a scheduled task you switched keeps its new state when you then search or filter the task list.
- **Startup:** Check with VirusTotal waits until the signatures are read, so no hashes of files signed by Microsoft are sent.
- **Graphics & DNS:** the link to the other network settings opens the Network group of the Tweaks page, also with a search or filter set. Before, it could open Security and system.
- **Install & update:** an Update all or Update that cannot start while another change runs keeps the result of Check for updates. Refresh or a language switch during an install keeps the row's progress and result.
- **Exports** (changes, Startup list, install media choices) replace the chosen file without following a link put in its place. A folder reached through a link is refused with a message that says so; choose the folder it points to.
- **Screen readers:** Undo, Apply again, Details, Remove and Reinstall in lists name their item.

## 1.5.0

More control over the Windows ISO, several tweaks at once, and Startup in tabs.

### Added

- **Windows install media:** remove optional Windows parts (WordPad, Recall, speech data), install apps at the first sign-in, a Smaller ISO option and Expert options such as device encryption off. Choices are remembered and can be saved to a file.
- **Windows install media:** a build that fails late keeps its work for up to 14 days and offers Resume or Discard.
- **Tweaks:** an impact filter, a Select mode for several tweaks at once, Num Lock at sign-in, and Ignore for a recommendation.
- **Install & update:** Check for updates shows only apps that have a newer version.
- **Remove apps:** several preinstalled apps at once; scheduled tasks have a search box.

### Changed

- **Windows install media:** a review before the build, every stage with its time, the ISO's SHA-256, and an app list that matches the image.
- **Startup:** tabs for sign-in apps, tasks, services, drivers and advanced places.
- **Long work** such as SFC, DISM and the ISO build says how long it can take.
- **Remove apps:** the Edge card says whether Windows lets you remove Edge on this PC.

### Fixed

- **Other accounts:** per-user paths, uninstallers and leftovers are the signed-in user's, also under a separate administrator account.
- **One window:** a renamed copy brings the open window to the front.
- Time limits for winget and PresentMon, clearer DISM errors, and smaller fixes for Search, Startup, Tools and screen readers.

## 1.4.0

Tunero now builds a debloated Windows 11 ISO, shows which programs use each device, and updates Chocolatey and Scoop apps too.

### Added

- **Windows install media:** a Windows 11 ISO from Microsoft's newest download or your own ISO, without the apps you tick and the account questions, if you like for unsupported PCs (Preview). At the first sign-in Tunero applies your profile's recommendations, each one undoable on Changes.
- **Live readings per program:** each device card names its busiest programs, with a sortable list, Open in Explorer, a priority rule and End task.
- **More package managers:** Update all also runs Chocolatey and Scoop and opens the Store's updates; a new card lists Chocolatey's outdated packages and update commands for developer tools.

### Changed

- **Website and README:** current screenshots, tunero.dev at the top of the README, and Chocolatey back as a way to install.
- **Icons:** no embedded content credentials (C2PA); they look the same.

### Fixed

- **Scan:** checks Windows does not answer for show "unknown" instead of disappearing; a tweak with nothing to change on this PC no longer says "already in place".
- **Install and Apply again:** closing or signing out waits for the install; the reapply task runs only from a folder users cannot change, and `choco uninstall` removes it.
- **Uninstall leftovers:** a folder shared with a program that is still installed is no longer offered.
- **Startup page:** a PowerShell entry behind a Windows path no longer passes as Microsoft's.
- **Undo:** the refresh rate fix returns at your current resolution, and Game Mode's original value comes back.
- Smaller fixes for Storage, Export and Import, messages and screen reader names.

## 1.3.0

Tunero can now install itself into Program Files on the first start, and it opens only one window.

### Changed

- **Install on the first start:** a signed release offers to put itself in Program Files (only administrators can change it there, so the reapply task can start it), add the Start menu entry and appear under Installed apps; "Run from here" keeps it where it is. Uninstall from Tunero or from Windows Settings removes the folder and the entry too.
- **One window:** starting Tunero while it is open brings the open window to the front, restored if minimized, instead of opening a second one. Runs with switches (command line, reapply task) are not affected.
- **Checking a download:** the README and the website name the code signature first (Properties > Digital Signatures) and the SHA-256 file second. The website's second download button also shows what to do when Windows warns.

## 1.2.3

Tunero.exe is now signed.

### Changed

- **Code signing:** Tunero.exe is signed with a Certum code signing certificate for open source developers, so Windows names the publisher, Open Source Developer Joshua Zürn, and Smart App Control lets the exe run. SmartScreen may still warn at first, until enough people have run it.
- **Website:** the answers on SmartScreen and Smart App Control match the signed releases and current Windows 11, where Smart App Control can be turned on again in Windows Security. The header links to the questions, and a click on Download points to what to do when Windows warns.

## 1.2.0

A new Storage page shows what fills your drives, and a progress card with Stop now shows over every page.

### Changed

- **Storage page** (under Clean up): Analyze reads a drive, straight from its NTFS file table when Tunero runs as administrator (about twice as fast as folder by folder in the test VM). It shows the drive as a folder tree beside a treemap, by file type and as its 100 largest files, and finds duplicate files when you ask. Files and folders can go to the Recycle Bin, each checked again when it is moved; Windows, program, game and AppData locations, profile folders such as Documents, links and OneDrive items are never offered, and the page says why.
- **Progress over every page:** applying or undoing changes, cleanups, uninstalls, app updates, SFC, DISM and the storage analysis show what runs and how far it is. Stop (Apply recommended, Apply again, Import, Cleanup) ends after the current step, so no change is left half done.
- **Website:** says what to do when Smart App Control blocks the exe.

### Fixed

- The result bar at the bottom of the window has a solid background, so the page no longer shows through it.
- Screen readers and voice control can open every page in the navigation and hear which one is open.

## 1.1.0

Tunero now covers more of what the tools it replaces do. Your tweaks can go to accounts created later and are applied again when Windows resets them (both off until you turn them on). The changes file can also carry removed apps, service start types and startup entries. Install & update searches all of winget, the program list finds folders of programs uninstalled long ago, the Startup page can delete files and tasks with a backup, and Tools has four more quick fixes. A scan result you keep on purpose can be ignored. Eleven new tweaks, a check that every release came from GitHub's release workflow, and the October .NET security update. Tested in Hyper-V on Windows 11 Pro and Home 26H2.

### Changed

- **Checking a download:** releases now carry a build provenance attestation, so `gh attestation verify Tunero.exe --repo jozuern/Tunero` shows that GitHub's release workflow built the exe from the tagged commit. Published releases cannot be changed afterwards.
- **New accounts get your tweaks:** "Copy to new accounts" on the Changes page writes the per-user tweaks you turned on with Tunero (Explorer, taskbar, privacy and others made only of registry values in your account) into the Default profile, the template Windows copies for every new account. The confirmation lists each value; Undo on the Changes page puts the Default profile back. Accounts that exist already keep their settings. The hive is loaded only for the change (never by a scan) and unloaded right after, so Windows can always create a profile; when it cannot be loaded (another program has it open), the copy fails with the reason; a hive left loaded by a Tunero that was ended during the change is unloaded at the next start. Tested in the VM: a new local account started with the copied values.
- **Export, three opt-in parts:** besides the tweaks and start priorities, the changes file can now carry the removed apps, the service start types and the startup entries turned off, each only when you tick it under "Also export" on the Changes page (or name it with `--include apps,services,startup` after `--export`). Import changes a service only where that PC's rules for it allow the change (also a service among the startup entries, so Windows' protected services stay as they are), turns off only startup entries that PC has (it never deletes one), and removes apps only after its own question that names every app, since removing cannot be undone. `--apply` lists the apps in the file and leaves them for the app. When a part of that PC cannot be read (the app list), only that part is skipped. Older versions read such a file and skip these parts.
- **Ignore a scan result:** a result you keep on purpose (a game library on a hard disk as a backup, or because copying is faster than downloading) can be ignored in its details. It leaves the list, the score and the problem count, nothing is recommended or applied for it (also not by `--apply-recommended`), and it stays under "Ignored results" on the Overview with Stop ignoring. Only that result is ignored, and only as it read: another library on a hard disk still shows, and the result comes back when it changes (a disk that could not be checked reports a problem, a problem turns critical, another disk takes its place).
- **Apply my changes again when Windows resets them** (Settings, off by default): a scheduled task starts Tunero two minutes after you sign in, as your account with administrator rights, and applies again only the changes a Windows update reset (of what "Apply again" offers; no Expert, boot-critical or anti-cheat sensitive change). A change you turned back yourself stays as you left it, and so does a setting two of your changes set differently. Each administrator account has its own task, and Uninstall in Settings removes it. Nothing keeps running. The task is a change on the Changes page, and undo removes it. It can only be turned on when Tunero is in a folder only administrators can change (Program Files, or winget or Chocolatey for all users), since the task starts it elevated without asking. The same run is `--reapply` on the command line.
- **Five right-click menu entries can go:** "Give access to", "Restore previous versions", "Open in Terminal", "Cast to Device" and "Share", one Explorer tweak each, as in Winaero Tweaker. Each blocks the shell extension behind the entry through Explorer's block list. Microsoft does not document the list; a VM test showed each entry gone from the menu and back after Undo, without an Explorer restart. "Give access to" also takes the Sharing tab out of Properties and "Restore previous versions" the Previous Versions tab (checked in the VM); their pages say so. "Include in library" ignores the list and stays.
- **Drive letters first and no "- Shortcut" suffix:** two Explorer tweaks, as in Winaero Tweaker. This PC shows "(C:) Local Disk" (after Explorer restarts), and new shortcuts are named without "- Shortcut". Microsoft does not document either value; a VM test showed both working and both gone again after Undo.
- **Faster submenus and a shorter hover time:** two new Personalize tweaks set the submenu delay to 100 ms and the hover time to 200 ms (Windows: 400 ms each) through SystemParametersInfo, the documented way: they take effect at once without signing out, and Undo sets the earlier values. Checked in the VM: the running session had the new values right away, and the old ones after Undo.
- **Two more documented policies:** "Start without Recommended" removes the Recommended section (recent files, new apps) from Start on Pro and up; a VM test on Windows 11 Pro confirmed that Pro honors it. "Click to Do off" turns off the screen analysis of Copilot+ PCs.
- **Start priorities, low memory priority:** a rule can now also give a program low memory priority (PagePriority), so Windows takes memory from it first when memory runs short; useful for a launcher or chat program that stays open next to a game. A VM test showed that Windows applies the value at program start (memory priority 1, 2 and 3 for those values, normal again without it).
- **Installed programs, unused program folders:** "Find unused program folders" looks in Program Files for folders of programs uninstalled long ago, as Bulk Crap Uninstaller does. A folder is listed only when no uninstall entry (also hidden components and Windows Installer records), service, autostart entry, App Paths entry, COM server, running program, shortcut or taskbar pin points into it (also by its short 8.3 name), it is not shared infrastructure (NuGet, Windows Kits and similar), its name matches no installed program or publisher, and nothing in it changed for 30 days. The folders appear under Left behind without a tick, with their size and last change, and go to the Recycle Bin only when you tick them.
- **Install & update, search winget:** besides the 91 reviewed apps, a search finds any package of the winget community repository by name or id (at most 50 results). Installing one asks first and says that Tunero has not reviewed it; it installs for the whole PC where the package offers that, otherwise winget opens for your account.
- **Startup page, Delete for files and tasks:** Delete entry now also works for a file in a Startup folder and for a scheduled task of another program, as in Autoruns. The file's content or the task's XML goes into the backup first, and Undo on the Changes page writes the file back or registers the task again under the same name and account; the undo confirmation names the file's size and the task's program. When the program puts a deleted entry back, Tunero leaves it (it is not a reset), and deleting it again keeps the new version for Undo. A file in your own Startup folder is a change of your account only. A task that keeps a stored password is not offered, since it could only come back with that password; Windows' own tasks stay on Services & tasks.
- **Tools, four more quick fixes:** "Rebuild the icon cache" for blank or wrong icons (Explorer ends, the signed-in user's IconCache.db is deleted as Microsoft's troubleshooting article does, Explorer starts again as that user). "Update Microsoft Defender" downloads the newest definitions now (Update-MpSignature). "Reset winget sources" restores winget's default sources when Install & update cannot reach the catalog; sources you added are removed, so it asks first. "Restart to Advanced startup" restarts into the menu where Startup Settings offers Safe Mode, for DDU for example; nothing in the boot settings changes, so the next start is normal again without a way back to set up.

### Fixed

- **.NET runtime 10.0.13:** the exe carries the .NET and Windows Desktop runtime of October 2026, Microsoft's security release ([release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.13/10.0.13.md)), instead of 10.0.12.
- **Widgets off, only where it works:** current Windows 11 lets no program write the Widgets policy while the User Choice Protection Driver runs, also not with administrator rights, so "Apply recommended" ended with a failed change on such PCs (found in the VM on 26H2 Home; with the driver's service disabled the same write succeeded). The tweak is now offered only where that driver is off, and its page points to Settings, Personalization, Taskbar.
- **Navigation badges:** the count next to Overview, Manual checks and Settings is now a round 16 px badge as in Windows 11; it was an upright oval.
- **Health, sensors:** fan headers and temperature inputs with nothing on them (0 RPM or 0 °C, and never more since the sensors were turned on) are no longer listed. A fan that stopped after it turned (zero fan mode at idle) still shows.
- **Update now:** the new version could refuse to start right after the update ("the native runtime folder is not the protected one"), because the old version passed its own restart marker on to it. It now starts, also when 1.0.0 runs the update.
- **Tools, quick fixes:** the confirmation of a fix now names its own action on the button ("Restart" for Restart to Advanced startup, not "Run"), and each fix's button carries the fix's name for screen readers instead of only "Run" or "Restart".
- **Startup page, undo of a deleted task:** when the task's folder in Task Scheduler had gone too (an uninstaller removed it), putting the task back failed with "file not found"; the folder is now created again.
- **Screen readers and voice control, clickable cards:** the cards that open details or a group were reported as a plain "pane" without a way to click them through UI Automation; they are now buttons that Narrator announces as such and Voice Access can click by name.
- **German, change targets and values:** targets that stayed English ("Task Scheduler\...", registry values changed bit by bit, a network adapter without a name) and stored states ("Disabled", a service's "Manual") now read in German in the confirmation, the explanation page and the change log, as the other targets already did.

## 1.0.0

The first release out of preview.

### Changed

- **New check, restart pending:** the scan now says when Windows waits for a restart to finish updates (Windows Update's RebootRequired) or a feature change (component based servicing's RebootPending), the two flags Configuration Manager shows as "Pending Restart". Restart before you change settings or compare speed. Queued file renames do not count: programs leave them behind all the time.
- **Command line, `--apply-recommended` and `--export`:** `--apply-recommended` applies the Overview's recommended list without the window, for the profile named with `--profile` (or the saved one), with the same rules as the button; changes that need their own confirmation are listed in the log. `--export <file>` writes the changes file of the Changes page. A profile this PC does not offer ends with the new exit code 9. Start priorities in an exported file now apply with `--apply` too, since they are no longer a preview.
- **Startup page, Delete entry:** like Autoruns, a value in a Run, RunOnce or policy Run key and the cmd.exe AutoRun can now be deleted, for example one whose program was uninstalled. Unlike Autoruns, Tunero backs the value up first: the Changes page lists the deletion and Undo writes it back with its type. RunOnce, policy Run and AutoRun entries had no switch before, so this is the first way to stop them here.
- **Installed programs, several at once:** tick programs and select "Uninstall selected" to remove them one after the other, as Bulk Crap Uninstaller does. With "Quietly where possible" on, Windows Installer programs and programs that register their own quiet command run without questions; the others show their window, and the next one starts when the program has left the list (or when you select Continue). Afterwards the page lists what the programs left behind: their folder in Program Files, ProgramData or AppData (also for Windows Installer programs that record no install folder) and Start menu or desktop shortcuts into it, but only folders no installed program, hidden component or service still uses. You review the list and move it to the Recycle Bin. A single uninstall gets the same check.
- **Program start priorities are no longer a preview:** a test in the VM confirmed that Windows applies them (a program with a rule for Below normal and low disk priority started with both, and with Normal again after Undo). They keep the "Undocumented value" badge, because Microsoft does not document the values.
- **Faster start:** the first scan result shows about half a second sooner (1.95 s to 1.4 s on a test PC). Scheduled tasks and shortcuts are read through their COM interfaces directly instead of late-bound calls, which compiled a binder on first use (the task list takes half as long, a shortcut a fifth). Memory compression and the DoH servers are read from the WMI classes behind Get-MMAgent and Get-DnsClientDohServerAddress instead of starting PowerShell twice per scan (milliseconds instead of about half a second each; the changes still run through the cmdlets). The tweak states are read in the same background step as the scan, while the window is still being drawn. The embedded catalogs are read on a background thread while WPF starts, instead of on the UI thread after it.

- **Health, live sensor readings:** instead of one long list of lines, every component (processor, graphics card, memory, mainboard, drives) has its own card with its main values set large: the hottest temperature, the total load with a bar, the fastest core clock, power and fan, each with the sensor it comes from and its peak since you switched the readings on. "All readings" opens a table of every sensor of that component by kind, with the value now and the peak. The values change in place every two seconds, so an open table stays open, and a line under the switch says when the sensors are still opening. The library's two memory entries share one card. New developer switch `--sensors on|all` for screenshots.

- **Windows 11 25H2 or newer:** Home and Pro 24H2 get no updates since 13 October 2026, so Tunero now supports 25H2 (build 26200) and newer. On 24H2 the app still starts, with a note that the version is no longer supported, so changes made there can still be undone; update Windows before you make new changes.
- **Startup page, scheduled tasks:** like Autoruns, the page now lists every task of other programs (an updater that runs daily, not only tasks at sign-in), and each program a task runs gets its own row, so a second program cannot hide behind a signed first one. Tasks that run a COM handler show the DLL behind it. Windows' own tasks (`\Microsoft\Windows\`) are listed but switched only on Services & tasks, where the catalog says which are safe to turn off: turning off TextServicesFramework\MsCtfMonitor, for example, stops typing in Start search.
- **Search, what Process Lasso does that Tunero leaves out:** live process rules (ProBalance, a power plan while a game runs, memory trimming) need a program in the background, and Windows has no setting that applies a processor affinity at every start; the search and docs/not-included.md now say so and point to the start priorities, which Windows applies itself.
- **Three more privacy tweaks** from documented Windows policies (Pro and up): Windows apps can no longer take screenshots of other windows or hide the capture border (a preview until a test shows the Snipping Tool and Game Bar keep working), Windows apps can no longer use presence sensing, and Windows no longer downloads speech model updates in the background. Microsoft Rewards in Edge stays out: its policy does not apply to profiles signed in with a Microsoft account, the only ones that see Rewards, and the search now says so.
- **Install & update, 57 more apps:** 91 apps in all, in two new groups (game streaming and recording; peripherals, lighting and audio) and the existing ones: Playnite, Heroic, itch, the Rockstar and Minecraft launchers, Prism Launcher, Modrinth, CurseForge, GeForce NOW, PS Remote Play, Moonlight, Sunshine, Streamlabs, CapFrameX, PresentMon, HWMonitor, CrystalDiskMark, System Informer, Process Explorer, Autoruns, Fan Control, SignalRGB, Logitech G HUB, Razer Synapse, SteelSeries GG, Voicemeeter, EarTrumpet, DDU, NVCleanstall, Notepad++, ShareX, Greenshot, Everything, WizTree, TreeSize, PowerShell 7, Windows Terminal, NanaZip, PeaZip, KeePassXC, Bitwarden, qBittorrent, Rufus, Ventoy, LibreOffice, PotPlayer, SumatraPDF, Signal, Telegram, Brave, LibreWolf, Vivaldi, the Visual C++ 2005 and 2008 runtimes and XNA. Each winget id and the install scopes its manifest offers were checked; per-user installers run as the signed-in user.
- **Startup page, export the list:** "Export list" saves every entry with its kind, state, publisher, signature, command, file and location as a CSV file in your language (semicolons where Excel expects them), for a forum post or to compare later. A cell that starts like a formula is written as text.
- **Startup page, more Autoruns locations:** the cmd.exe AutoRun, Explorer add-ons (icon overlays, shell service objects, shared task schedulers, copy hooks), context menu handlers registered for your account only, changed open commands of programs and batch files, SilentProcessExit monitors, sign-in providers, AppCertDlls, DirectShow filters, the 32-bit Image File Execution Options and Winlogon values Windows does not set. They are listed with their file and signature; the ones Windows never sets are marked for a look.
- **Update all apps:** programs installed for the whole PC update in the app as before; programs installed only for your account now update as you, in a winget window of their own, instead of with administrator rights (which installed them for the wrong account, or failed). A failed update says "Update failed" instead of "Install failed".
- **Backup update key:** every version now also accepts updates signed with a second key that is kept offline, so losing the release key would not strand installed copies. The release workflows check against both keys.
- **Sources are links:** the numbered sources at the end of every explanation page open in your browser (as the signed-in user, never as administrator) and can be reached with the keyboard; before, the address was only in a tooltip.
- **High contrast:** with a Windows contrast theme on, the app uses it (Windows' own colors, no Mica) instead of the Fluent light or dark colors, and follows when you turn it on or off.
- **Small screens:** the window and the confirmation dialog fit the screen's free area: on a laptop at 150 % (about 1280 x 672) the window no longer reaches under the taskbar, and the list of changes in the confirmation scrolls so its buttons stay on screen.
- **Accessibility:** the search and key boxes have names for screen readers, and dialogs carry their title as the window title (Alt+Tab, screen readers).
- **Signing out during a change:** Windows is asked to wait until the change has finished, as closing the window already did.
- **Delivery Optimization cleanup** asks once before it deletes, like the other cleanups.
- **README and website:** they say "every setting it changes can be undone" instead of "everything" (removed apps and deleted files are the exception), mark the winget and Chocolatey commands as pending until the first version has passed review, describe the Startup page by its locations instead of "everything that starts with Windows", mark program start priorities as preview, state how far the tests reach, and list what Tunero does not replace (a driver cleaner, live per-process rules, security trade-offs, installing Windows).
- **Release notes** are the version's section of this changelog; a release without one stops before it is drafted.
- **Command line:** `--apply` and `--undo-all` no longer show a message box when the start is refused for safety (a profiler variable, or a data or runtime folder other accounts can change); they end with the new exit code 8, so a script does not wait on a dialog.

### Fixed

- **Keyboard and screen readers:** Tab no longer stops on cards and lists that do nothing (a screen reader read them as an empty "pane" or "list", and on Tools every card before its own button), the group headings in the navigation are no Tab stops and are read by their text instead of a control type name, and the navigation button, the window buttons and the explanation in the details pane have names. Checked on every page and in the confirmation: every stop now has a name. Screen readers also reach the switches and buttons inside list rows (Startup, Services, Tweaks, Installed programs and the other lists): UI Automation showed each row as an empty item, so Narrator's scan mode and Voice Access could not find them.
- **Change log after an undo:** undoing a change built for this PC (a deleted or switched-off startup entry, a service, a device setting, a fix from the scan) logged its internal id, such as `startup.delete.runkey.runuserrun...`, on the Changes page. It now shows the title, as in the list above it ("Delete startup entry: Steam").
- **Small screens:** at 1920 x 1080 with 150 % scaling (a common laptop setting) the navigation rail cut off its last page, Manual checks, under the footer. On short windows the pages now take the compact item height of Windows Settings.
- **System info, memory:** without a reported speed (virtual machines, some firmware) the tile read "Unknown,  MT/s"; it now leaves the speed out.
- **Contrast themes:** text in the accent color (the "Recommended" lines on the Tweaks page, the place of a search result) stayed dark blue on black. It now uses Windows' link color, like Windows' own apps.
- **Undo all and `--undo-all`** passed over a backup they could not read (damaged, locked by another program, or written by a newer version), so the change it records stayed in place without a word. It is now reported as not undone, with the file and the reason.
- **Restore point:** within Windows' limit (one restore point a day unless the limit is lifted) Windows skips a new one and still reports success, so the app counted an older restore point as made for this change. It now checks the limit first and asks you, as when System Protection is off.
- **Backup not updated after a change:** when the backup could not be updated after an apply (the originals are saved before the first write), the result said only "Applied". It now says so; Undo still restores the original values.
- **Startup page, shell extensions:** one blocked in your account's own list (HKCU) showed as on. It now shows as off, and turning it on clears it there.
- **Scans** read the backup folder once instead of once per tweak, and the history of undone changes keeps its newest 500 files instead of growing with every undo.
- **Undo across updates is tested:** backup files now record their format, and the tests restore a saved backup of every kind of change (registry, services, power plans, boot options, tasks, DNS, network adapter, NVIDIA and the others) the way an earlier version wrote it. A backup from a newer version of Tunero is left alone instead of being misread, and the stored catalog version now changes with every catalog change.
- **A setting that could not be read** during an apply was skipped, and the change was still reported as applied. The apply now stops before it writes anything and says which setting it could not read.
- **Power plan undo after "Apply again":** when you had switched to another plan and applied Tunero's plan again, undo switched back but left the plan Tunero created in Power Options. It is removed again.
- **Backups not owned by Administrators** counted as "no backup", so undo reported success without restoring anything, and the next start deleted the file. Every backup is now given to the Administrators group when it is saved (or the change is not made), and a file owned by another account blocks apply and undo of that change instead.
- **Network adapter undo** could fail for a change made with an adapter that is no longer connected, when the driver spelled a keyword differently.
- **Undo all** now also restores the settings of the two "permanent" AI tweaks (what they deleted does not come back), so undoing everything before uninstalling no longer leaves them, and it reports a change it cannot undo instead of skipping it silently.
- **Expert mode:** its question no longer opens inside the switch while it is still being set; screen readers could hang on it.
- **Settings file:** one that cannot be read is kept as settings.json.bad instead of being overwritten with the defaults at the next save.
- **Explanation pane:** an explanation that failed to build once is built again on the next selection, and a check without a page says so in your language.
- **Sensors** are closed when the app ends.
- **Location:** "Location service off for everything" and "Location access off" wrote the same policy value, so undoing one left a false "reset by Windows" note on the other. The first is no longer offered; changes made with it can still be undone.
- **NVIDIA settings undo** while the driver was being updated reported success and dropped the originals; it now fails and can be repeated.
- **Gaming power plan undo** could delete a plan of the same name that you made yourself, when the app could not tell which plan it had created; it now leaves such plans alone.
- **Error reports** no longer contain your account's security id (SID) from registry messages.
- **Self-update:** when the new version could not start after the update (antivirus took the new exe away or blocked it), only the renamed old exe was left and the shortcut started nothing. The version you were using is now put back, and the message says so. The new version also no longer inherits a profiler or startup hook from the user's environment.
- **Startup page, services:** without a valid signature (the signature service turned off, a failed check) a Windows service could become switchable here in Expert mode; the Services page's rule, which counts a file in the Windows folder as Windows' own, now applies here too.
- **Startup page, script hosts:** a variable of your own account could make a script look like it lives in System32, so the entry was hidden as Microsoft's. The check now uses the machine's variables only.
- **A hung part of the scan** (a startup entry on a network share that does not answer, a stuck WLAN service or WMI query) emptied the whole extra section of the scan for up to two minutes, and with it most checks. Each part now has its own time limit of 30 seconds and only its own result stays empty; a probe that did not answer is not started a second time by "Scan again". The memory compression read and the DNS change have time limits too.
- **Memory integrity check:** when Windows could not report it, the check said memory integrity was off. It now says unknown.
- **Ethernet speed check:** a link at 100 Mbit/s that the adapter negotiated by itself is shown as a note instead of a problem: often the other end (a router port, a powerline adapter, an older switch) is the limit, which the scan cannot see. A speed forced lower in the driver stays a problem.
- **Games on a hard disk:** two game libraries on the same hard disk counted as two problems; they are now one finding for the disk.
- **Start menu entry:** an old entry that points to a folder or a web address could stop the app at start while it renamed old entries.
- **Virtual machines** on Amazon EC2 and Google Compute Engine are recognized as such, so the checks for real hardware are skipped there.
- **Import:** a file with two or more changes that need their own confirmation (Expert, preview or anti-cheat sensitive) applied only the first one and refused the others as "busy", because each change starts a scan. A change now waits for the running scan instead.
- **Ryzen AI 300 laptops** (for example the Ryzen AI 9 HX 370) were taken for a Ryzen X3D with two chiplets, because their two core clusters have L3 caches of different size. The app then blocked the High performance plan, reported a problem with impact 4 and guarded the Xbox Game Bar. Only processors named X3D count now.
- **A failed scan** was never shown: the Overview kept saying "The scan is reading this PC". The verdict now says the scan could not finish, and the result bar shows the error.
- **Leftovers check:** the boot option useplatformclock set to No counted as a problem; only Yes forces the slower timer.

## Before 1.0

Eleven preview releases, from 0.4.0 on 2026-10-10 to 0.9.14 on 2026-10-13, led up to 1.0. In short:

- **0.4.0 to 0.5.0:** every tweak, check and text reviewed against Microsoft and vendor documentation; 15 tweaks without evidence removed (reasons in [docs/not-included.md](docs/not-included.md)); the Preview marker for tweaks not yet tested on real Windows; a protected data folder; fixes from the first VM test run and a code audit.
- **0.6.0 to 0.6.8:** an interface pass for public use: tweaks in groups, the details pane, results in one result bar.
- **0.7.1:** Export and Import of your changes, new tweaks, and security fixes for what the app runs as administrator.
- **0.7.3:** open source under the GPL 3.0 (releases up to 0.7.1 were PolyForm Strict 1.0.0), and the app can remove itself.
- **0.8.0:** PCOptimizer is renamed Tunero; Chocolatey and Scoop packages next to winget.
- **0.8.8 to 0.9.14:** checks for memory integrity and a G-SYNC frame limit, update hardening, a new website, the rating mark and an Overview that leads with its verdict.

The full notes of every preview are in the [changelog as of 1.3.0](https://github.com/jozuern/Tunero/blob/v1.3.0/CHANGELOG.md#0914-preview) and on the [releases page](https://github.com/jozuern/Tunero/releases).
