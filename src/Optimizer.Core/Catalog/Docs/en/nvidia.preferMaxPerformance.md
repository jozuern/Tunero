# NVIDIA power mode: prefer maximum performance (global)

## Summary
Keeps the graphics card at maximum performance while most 3D applications run. Can steady frame times in light games, raises power use. Per game is usually better.

## How it works
By default the driver lowers the graphics clock when the load is low. "Prefer maximum performance" in the global profile tells it to use the GPU at maximum performance while most 3D applications run [1]. The app writes the value from NVIDIA's settings header through the driver settings API [2].

## Why it can help
In light or older games the card can drop to lower clocks between frames and needs time to clock up again, which can cause uneven frame times.

## Evidence
The effect is situational; in demanding games the card already runs at full clocks and nothing changes.

## Trade-offs & risks
Higher power draw, heat and fan noise, also with browsers or launchers that use 3D acceleration.

## When not to use it
Prefer the per-game version on the Graphics & DNS page, which only affects the games you choose.

## Sources
1. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
2. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
