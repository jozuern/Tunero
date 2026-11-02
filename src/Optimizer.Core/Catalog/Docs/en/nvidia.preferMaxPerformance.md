# NVIDIA power mode: prefer maximum performance (global)

## Summary
Keeps the graphics card at maximum performance while most 3D applications run. Meant to steady frame times in light games, not measured yet; raises power use. Per game is usually better.

## How it works
By default the driver lowers the graphics clock when the load is low. "Prefer maximum performance" in the global profile tells it to use the GPU at maximum performance while most 3D applications run [1]. The app writes the value from NVIDIA's settings header through the driver settings API [2].

## Why it can help
In its adaptive mode the driver lowers the GPU's performance level when a 3D application needs less [1]. In light or older games that can mean lower clocks between frames; keeping the card at maximum performance is meant to avoid uneven frame times while it clocks up again.

## Evidence
The effect is situational; in demanding games the card already runs at full clocks and nothing changes. No measurement shows an effect on frame times yet.

## Trade-offs & risks
Higher power draw, heat and fan noise, also with browsers or launchers that use 3D acceleration.

## When not to use it
Prefer the per-game version on the Graphics & DNS page, which only affects the games you choose.

## Sources
1. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
2. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
