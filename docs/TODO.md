# Open items

What is still open: it needs a person, a VM image or hardware we do not have, or it is planned work.

## Release channels and the VM plan

- **winget:** the first version (pull request #450585, `jozuern.Tunero` 0.8.0) is still in review. Chocolatey offers the current version and Scoop's bucket follows every release.
- **New VM plan rows** (section 1 and 6 of [vm-test-plan.md](vm-test-plan.md)): a failed start after a self-update (needs a signed release newer than the installed one), and the code signing rows with a signed release.

## Checks in the VM (from earlier runs)

- **`privacy.locationOff` on Home editions:** the tweak uses the documented policy "Turn off location" (`DisableLocation`), proven on Pro, and is left out on Home. A run on PCO-Home (2026-10-14) was not conclusive: location was already off there, and the location API answers Denied from the test task with and without the policy. Turn location on by hand at the console, set the policy and look at Settings; if Windows honors it, drop the edition condition.
- **`focus.lockScreenTipsOff` turns off Windows spotlight:** check whether writing only `SubscribedContent-338387Enabled` avoids it. Needs an activated Windows (Spotlight settings are locked without it); the test script mode `spot` is ready.
- **Scroll bar arrows in dialogs:** UI Automation names them "CaretUp24" and "CaretDown24" (WPF-UI's icon names), so a screen reader reads those. Give them real names or hide them from UI Automation.
- **Need real hardware or traffic capture:** `gpu.windowedOptimizations` (discrete GPU), `display.autoHdrOn` and `display.vrrOn` (HDR or VRR display), `network.dohAutoUpgrade`, the NVIDIA settings, MSI mode and interrupt affinity, the network adapter properties (section 5 of the VM plan).
- **V-Cache driver:** the service name `amd3dvcacheSvc` needs a check on an X3D PC.
- **Num Lock at sign-in:** left out until a VM run checks it at the sign-in screen (an undocumented value that only shows there).

## Left from the audit of 2026-10-19

The audit's other findings are fixed (commits `dde41d6` to `2a931a0`, and `1de6875` for the docs). Left on purpose:

- **One process runner in `ActionContext`:** `StreamingProcess` is static and called directly by the winget, SFC/DISM and PresentMon flows, and `IProcessRunner.Run` has a timeout but no cancellation. One `RunAsync(file, args, timeout, CancellationToken, IProgress<string>)` would give those flows fakes and tests, a Stop button for long DISM runs and one timeout policy.
- **Install, update and setup behind seams:** `UpdateService` and the setup decision in `MainViewModel.Setup.cs` are not injectable, so the setup decision and the update path beyond `Updater.RollBack` have no tests.
- **One path expander and one CLI verb table, with `ChangeSession` owning the change lock:** per-user strings are expanded four ways (`Programs.Command` and `ProgramLeftovers.CleanPath` use the elevated process's environment), and the cross-process `ChangeLock` is taken by the callers, so a new entry point could skip it.
- **User-scope writes without a resolved SID** fall back to the elevated account without the "other account" banner (a domain account without a domain controller; cannot be tested here).
- **Single instance by process name:** `Tunero (1).exe` opens a second window; telling the copies apart needs another way to identify the running one.
- **Workflows:** the verify logic is copied inline into the workflows, and `pages.yml` does not wait for CI.
- **Dead code:** the unused `Preflight` parameter, the test-only `MftRecord.ExtensionReferences` and `WingetPackage.IsInstalledWithWinget`, the `OptionalFeatures` ids without `TweakIds.Slug` (changing them needs a legacy-id fallback), the split quick fixes and the copied `Text(lang)` helpers.
- **Test tooling majors** (Test SDK 18, xunit runner 4): left to Dependabot.
