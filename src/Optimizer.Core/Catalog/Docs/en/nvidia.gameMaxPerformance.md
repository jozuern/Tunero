# NVIDIA: prefer maximum performance for this game

## Summary
Sets "Prefer maximum performance" only in this game's NVIDIA profile. Keeps clocks up while the game runs, everything else stays as it is. An effect on frame times is not measured yet.

## How it works
NVIDIA stores settings per game in driver profiles [1]. The app writes the power management mode into the profile this game belongs to. "Prefer maximum performance" uses the GPU at maximum performance while most 3D applications run [2]. If the driver has no profile for the game, the app creates one named "Tunero: " plus the game's file name. Undo puts back the value the profile had before, or removes the setting if it had none.

## Why it can help
In its adaptive mode the driver lowers the GPU's performance level when a 3D application needs less [2]. In light or CPU-bound games that can mean lower clocks between frames; keeping the card at maximum performance is meant to avoid uneven frame times.

## Evidence
The effect is situational; in demanding games the card already runs at full clocks. No measurement shows an effect on frame times yet.

## Trade-offs & risks
More power use and heat while the game runs. If the app created a profile for the game, undo deletes it again as long as it holds no other settings.

## When not to use it
On laptops on battery, or in games that already run at full GPU load.

## Sources
1. https://archive.docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__drsapi.html
2. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
