# Frame limit below the G-SYNC refresh rate

## Summary
Sets the global NVIDIA frame rate limit 3 FPS below the refresh rate of the G-SYNC monitor, so games faster than the monitor do not get V-Sync delay.

## How it works
NVIDIA stores driver settings in profiles: a global profile and one per game. The app writes the documented setting "Frame Rate Limiter" (FRL_FPS) into the global profile through NVIDIA's driver settings API [1][2]. Games with their own value in their profile keep it. Undo restores the previous value, or removes it if the profile had none.

## Why it can help
With G-SYNC on and V-Sync forced on, a game that runs faster than the refresh rate hits the G-SYNC ceiling, and V-Sync takes over. Blur Busters (a third-party test site) measured with a high-speed camera that double-buffered V-Sync then adds 2 to 6 frames of delay [3]. A limit 2 FPS below the refresh rate avoided it at every refresh rate they tested, and 3 FPS below is their recommended margin [4].

## Evidence
Measured by Blur Busters in 2017 with a G-SYNC module monitor on Windows 10; the mechanism (V-Sync buffering at the refresh ceiling) is unchanged. That the NVIDIA App shows this setting as "Max Frame Rate" is not yet confirmed on a real PC, so the fix carries the badge "Not yet tested on a real PC".

## Trade-offs & risks
The limit applies to every game without its own value. With G-SYNC that costs nothing visible, because frames above the refresh rate cannot be shown without delay anyway. An in-game limiter or NVIDIA Reflex works with less delay; use those where a game has them.

## When not to use it
Without G-SYNC, or when you use a different global limit on purpose.

## Sources
1. https://archive.docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__drsapi.html
2. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
3. https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/4/
4. https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/5/
