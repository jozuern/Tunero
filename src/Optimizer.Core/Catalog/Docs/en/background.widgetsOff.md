# Widgets off

## Summary
Turns off the Widgets board and its news feed for the PC via policy, including the taskbar entry.

## How it works
The policy AllowNewsAndInterests = 0 (Allow widgets) disables the whole widgets experience, including content on the taskbar [1]. Current Windows 11 lets nothing but Windows itself write this value while the User Choice Protection Driver (UCPD) runs, also not administrators: a VM test on 26H2 Home was denied with the driver running and succeeded with its service disabled. Tunero therefore offers the tweak only on PCs where that driver is off. Elsewhere, turn Widgets off in **Settings > Personalization > Taskbar**.

## Why it can help
The Widgets board loads web content; with the policy set, that content is no longer loaded.

## Evidence
No published measurement shows a frame rate or memory effect. The benefit is mainly fewer distractions.

## Trade-offs & risks
No weather or news on the taskbar.

## When not to use it
Keep Widgets if you use them.

## Sources
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-newsandinterests
