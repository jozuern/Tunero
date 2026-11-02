# Auto HDR on

## Summary
Older DirectX 11 and 12 games that only output SDR are shown with HDR on an HDR display. HDR itself must be on.

## How it works
Auto HDR raises color range and brightness of SDR games that use DirectX 11 or 12 on an HDR-capable display; it is under Settings > System > Display > HDR [1]. The app sets the AutoHDREnable token in DirectXUserGlobalSettings, next to the windowed game optimizations. Microsoft describes the switch [1] but does not document the registry value behind it; the app writes the value Windows itself stores for the switch. Until a test on real Windows confirms the effect, the tweak is a Preview.

## Why it can help
Older games look closer to HDR games on an HDR monitor.

## Evidence
A visual choice. Microsoft states that turning on Auto HDR also turns on the optimizations for windowed games [2], which change how DirectX 10 and 11 games in a window present their frames. Auto HDR itself takes some GPU compute power, which Microsoft does not expect to significantly affect games [3]; it publishes no frame rate measurement.

## Trade-offs & risks
The result depends on the game; some look washed out or too bright. Without an HDR display or with HDR off, nothing changes.

## When not to use it
If you have no HDR display, or prefer games in their original look.

## Sources
1. https://support.microsoft.com/en-us/windows/hardware/display-graphics/use-auto-hdr-for-better-gaming-in-windows
2. https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952
3. https://devblogs.microsoft.com/directx/auto-hdr-preview-for-pc-available-today/
