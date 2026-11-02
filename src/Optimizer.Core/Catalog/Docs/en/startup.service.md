# Service at startup

## Summary
Sets this service to start on demand (Manual) instead of with Windows. A Windows service only where Tunero explains it.

## How it works
The start type is changed to Manual in the Service Control Manager [1]; the service is not disabled, so its program can still start it. Undo restores the previous start type.

## Why it can help
Programs that start with Windows make signing in slower and use memory and sometimes processor time in the background.

## Evidence
Signing in gets faster; the frame rate effect depends on what the program does in the background.

## Trade-offs & risks
Features of the program that need the service right after startup (for example update checks or device detection) may start later or not at all.

## When not to use it
For anti-cheat, security, VPN and driver services.

## Sources
1. https://learn.microsoft.com/en-us/windows/win32/services/service-startup
