# Allow more than one restore point per day

## Summary
Internal: lets the app create a restore point before changes even if another one was created in the last 24 hours.

## How it works
Windows skips new restore points within 24 hours of the last one. SystemRestorePointCreationFrequency = 0 removes that limit [1]. The app sets this itself before its first change in a session. It is not in the tweak list, but the Changes page shows it as its own entry, and "Undo all" removes it again.

## Why it can help
Makes sure there is a fresh restore point before the app changes anything.

## Evidence
Not a performance setting.

## Trade-offs & risks
Programs that create restore points can create more of them; System Protection's size cap still applies.

## When not to use it
No reason not to use it while using this app.

## Sources
1. https://learn.microsoft.com/en-us/windows/win32/sr/calling-srsetrestorepoint
