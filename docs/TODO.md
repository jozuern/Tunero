# Open items

What is still open: it needs a person, a VM image or hardware we do not have, or it is planned work.

## Release channels and the VM plan

- **winget:** the first version (pull request #450585, `jozuern.Tunero` 0.8.0) is still in review. Chocolatey offers the current version and Scoop's bucket follows every release.
- **New VM plan rows** (section 1 and 6 of [vm-test-plan.md](vm-test-plan.md)): a failed start after a self-update (needs a signed release newer than the installed one), and the code signing rows with a signed release.

## Checks in the VM (from earlier runs)

- **`privacy.locationOff` on Home editions:** the tweak uses the documented policy "Turn off location" (`DisableLocation`), proven on Pro, and is left out on Home. A run on PCO-Home (2026-10-14) was not conclusive: location was already off there, and the location API answers Denied from the test task with and without the policy. Turn location on by hand at the console, set the policy and look at Settings; if Windows honors it, drop the edition condition.
- **`focus.lockScreenTipsOff` turns off Windows spotlight:** check whether writing only `SubscribedContent-338387Enabled` avoids it. Needs an activated Windows (Spotlight settings are locked without it); the test script mode `spot` is ready.
- **Need real hardware or traffic capture:** `gpu.windowedOptimizations` (discrete GPU), `display.autoHdrOn` and `display.vrrOn` (HDR or VRR display), `network.dohAutoUpgrade`, the NVIDIA settings, MSI mode and interrupt affinity, the network adapter properties (section 5 of the VM plan).
- **V-Cache driver:** the service name `amd3dvcacheSvc` needs a check on an X3D PC.
- **Num Lock at sign-in:** left out until a VM run checks it at the sign-in screen (an undocumented value that only shows there).
