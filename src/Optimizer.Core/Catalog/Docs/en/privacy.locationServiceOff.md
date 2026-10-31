# Location service off for everything

## Summary
Turns the Windows location service off for all apps, Search and Windows, and locks the settings. Older entry with the same policy as "Location access off". Pro, Enterprise and Education.

## How it works
The policy "Turn off location" (DisableLocation = 1) is Force Location Off: all location privacy settings are turned off and grayed out, and no app may use the location service, including Search [1][2]. Switching back to user control restores each app's earlier setting [1].

## Why it can help
No app or Windows feature can read where the PC is, and Settings cannot override it. The tweak "Location access off" now sets the same policy; this entry stays so that changes made with it earlier can be undone.

## Evidence
A documented Windows policy [1][2]. A privacy setting without effect on frame rate or latency.

## Trade-offs & risks
Apps and Windows features that use your location, such as maps, weather and Find my device, can no longer get it. Undo returns control to Settings [1].

## When not to use it
If you use location in maps, weather or Find my device.

## Sources
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system
2. https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
