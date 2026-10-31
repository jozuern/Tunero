# Reset limiting NVIDIA global settings

## Summary
Resets the NVIDIA global settings that limit games (frame rate limit, power mode) to the driver defaults; with G-SYNC, the limit goes just below the refresh rate instead.

## How it works
NVIDIA stores driver settings in profiles: a global profile and one per game. The app removes the listed settings from the global profile through NVIDIA's driver settings API [1]. Game profiles stay as they are. Undo writes the previous values back.

With G-SYNC on and V-Sync forced on, it does not remove a low frame rate limit but sets it 3 FPS below the refresh rate: without a limit, a game faster than the monitor would get V-Sync delay, and Blur Busters (a third-party test site) found 3 FPS below the refresh rate the safest margin [4].

## Why it can help
A global frame rate limit far below the refresh rate, or the power mode forced to minimum, slows down every game. Without them, each game runs as the driver intends.

## Evidence
A frame rate limit caps the frame rate by design, and the minimum power mode keeps the graphics card at lower clocks; removing them restores the normal behavior. The NVIDIA Control Panel lists Max Frame Rate as off by default [2], and the driver settings header defines the power mode default as optimal power [3].

## Trade-offs & risks
If you set the limit on purpose, for example to reduce heat or noise, you lose that. Set it per game in that case.

## When not to use it
When you deliberately use a global frame limit.

## Sources
1. https://docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__drsapi.html
2. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
3. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
4. https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/5/
