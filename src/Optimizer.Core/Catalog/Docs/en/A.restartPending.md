# Restart pending

## Summary
::: variant update
Windows Update waits for a restart to finish installing updates. Restart before you change settings or measure performance.
:::
::: variant servicing
Windows waits for a restart to finish adding or removing a feature or component. Restart before you change settings or measure.
:::
::: variant default
Checks whether Windows waits for a restart to finish updates or a feature change.
:::

## Why it matters
Updates and features often have to replace files that are in use. Windows then finishes the work at the next restart [1]. Until then the update is not complete: the old files are still in use, and other installers can refuse to run [1]. Measurements and changes made in this state can be undone by what happens at the restart, so compare speed only after it.

## How we detected it
We look for the two keys Windows sets while it waits: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` for Windows Update and `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending` for adding or removing Windows features [2][3]. Pending file renames are not counted: programs queue them often, and they do not need a restart soon.

## How to fix
::: variant update
1. Save your work and restart through Start > Power > **Update and restart**.
2. Run the scan again afterwards. Some updates need a second restart [1].
:::
::: variant servicing
1. Save your work and restart the PC.
2. Run the scan again afterwards. If this still shows after two or three restarts, an installation did not finish: install the most recent updates or the feature again [1].
:::
::: variant default
Nothing to do.
:::

## How to check the fix
Run the scan again after the restart. Both lines under "What we found" should show "No".

## Sources
1. https://learn.microsoft.com/en-us/exchange/plan-and-deploy/deployment-ref/ms-exch-setupreadiness-pendingrebootwindowscomponents
2. https://learn.microsoft.com/en-us/intune/configmgr/core/clients/manage/manage-clients
3. https://learn.microsoft.com/en-us/intune/configmgr/core/servers/deploy/install/list-of-prerequisite-checks
