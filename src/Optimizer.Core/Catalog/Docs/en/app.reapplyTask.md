# Apply changes again after Windows resets them

## Summary
Two minutes after you sign in, Tunero starts on its own for a moment and applies those of your changes again that a Windows update has reset, then ends.

## How it works
Turning this on registers a scheduled task for your account in Task Scheduler's Tunero folder; another administrator who turns it on gets their own. It starts when you sign in, after a delay of two minutes [1][2], as your account with administrator rights ("run with highest privileges") [3], so it needs no confirmation. It runs Tunero with `--reapply`: Tunero scans the PC like the Overview, takes the changes it lists as reset that "Apply again" offers, keeps those reset since a Windows update (the Windows version changed after you applied them, or after Tunero last found them in place), and applies them again, then ends. A change you turned back yourself in Windows stays as you left it, and so does a setting two of your changes set differently (a tweak and the Services page on one service); the Overview shows them for you to decide. Expert, boot-critical and anti-cheat sensitive changes are never applied this way; they stay on the Changes page for you to decide. Nothing else changes, and no Tunero process keeps running. The task is a change like any other: the Changes page lists it, and undo removes it.

The task can only be set up when Tunero's exe is in a folder only administrators can change (Program Files, or a machine-wide winget or Chocolatey install): it runs the exe with administrator rights without asking, and an exe in Downloads could be replaced by any program you run.

## Why it can help
A feature update or a repair of Windows can put values back to Windows' defaults. Without the task, the Overview shows them as reset the next time you open Tunero; with it, they are back without a visit.

## Evidence
No speed effect of its own: it only keeps the effect of the changes you already chose. The run takes a few seconds after sign-in, about as long as a scan.

## Trade-offs & risks
A change Windows reset on purpose (a new default that fixes a problem, or a setting an administrator's policy controls) comes back too. Each run that applies something adds a line to the change log and, with System Protection on, a restore point. If you move or delete the exe, the task fails until you turn it off and on again from the new place. Uninstalling Tunero in its Settings removes the task.

## When not to use it
When you want to see what an update reset before anything is applied again: use "Apply again" on the Overview instead. On a PC that other people manage.

## Sources
1. https://learn.microsoft.com/en-us/windows/win32/taskschd/logon-trigger-example--xml-
2. https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-delay-logontriggertype-element
3. https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-runlevel
