# Set power mode to best performance

## Summary
Sets the Windows power mode to "Best performance" for mains power, the same as the slider in Settings > System > Power.

## How it works
The app sets the power mode overlay that the Settings slider uses [1]. Windows keeps separate modes for battery and mains power; this changes the one for the current power source. Undo restores the previous mode.

## Why it can help
"Best performance" lets the processor raise its clock faster and use more power, which helps in processor-heavy games.

## Evidence
Microsoft documents that each slider position applies a different set of processor power settings [1]. How much frame rate that gains is not measured and depends on the PC. On PCs without a battery, Windows has no built-in values for the modes; they come from the PC maker [1], so the app rates this fix lower on desktops than on laptops.

## Trade-offs & risks
More power draw, heat and fan noise. On a desktop the difference to Balanced is usually small.

## When not to use it
If quiet operation matters more than frame rate.

## Sources
1. https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/customize-power-slider
