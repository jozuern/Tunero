# OneDrive file sync off

## Summary
Stops OneDrive from syncing files and hides it in File Explorer, without uninstalling it. Files only in the cloud are not available on the PC. Pro, Enterprise and Education.

## How it works
The policy "Prevent the usage of OneDrive for file storage" (DisableFileSyncNGSC = 1) means users cannot access OneDrive from the OneDrive app and file picker, Store apps cannot reach it, OneDrive is gone from the File Explorer navigation pane, files are no longer kept in sync and camera roll uploads stop [1].

## Why it can help
No sync traffic or OneDrive processes in the background, also during games, while OneDrive stays installed for later.

## Evidence
A documented Windows policy [1]. How much it saves depends on how much you sync.

## Trade-offs & risks
Files that are only in the cloud cannot be opened from this PC, and changes are not synced. If OneDrive backs up your Desktop, Documents or Pictures folders, those folders stay in the OneDrive folder: new files there are no longer backed up, and online-only files in them cannot be opened. The setting applies to every account on this PC. Undo turns sync back on. To remove OneDrive completely, use the Remove apps page instead.

## When not to use it
If you use OneDrive, or files you need are online-only.

## Sources
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system
