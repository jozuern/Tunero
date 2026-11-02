# Clipboard history on

## Summary
Turns on clipboard history: Windows + V shows the items you copied earlier, so you can paste any of them again.

## How it works
The app sets EnableClipboardHistory to 1, the per-user value behind Settings > System > Clipboard > Clipboard history [1]. Microsoft describes the switch [1] but does not document the registry value behind it; the app writes the value Windows itself stores for the switch. Until a test on real Windows confirms the effect, the tweak carries the badge "Not yet tested on a real PC".

## Why it can help
Copying several things between documents no longer means switching windows back and forth. Pinned items stay available for text you paste often.

## Evidence
A standard Windows feature [1]. The gain is convenience, not speed.

## Trade-offs & risks
The history keeps what you copy, including passwords copied from text. Unpinned items are cleared when the PC restarts. Syncing the history to other devices is a separate setting; the app does not change it. Cannot be combined with "Clipboard history off", which blocks the history by policy [2].

## When not to use it
On a shared PC, or if you often copy sensitive data and do not want it kept in a list.

## Sources
1. https://support.microsoft.com/en-us/windows/clipboard-in-windows-c436501e-985d-1c8d-97ea-fe46ddf338c6
2. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
