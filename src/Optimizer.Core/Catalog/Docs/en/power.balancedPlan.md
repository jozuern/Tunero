# Switch to the Balanced power plan

## Summary
Switches to Balanced, the Windows default: instead of Power saver, the CPU reaches full clock again; in the Quiet and cool and Battery profiles, it replaces High performance for less heat and noise.

## How it works
Windows power plans set how the processor raises and lowers its clock, when it parks cores and how devices save power. Power saver delivers reduced performance to save power; Balanced matches performance and power consumption to demand; High performance, and plans based on it such as Ultimate Performance, deliver maximum performance at the expense of higher power consumption [1]. In the Windows defaults, Power saver also parks processor cores on mains power, which Balanced does not.

## Why it can help
A game on a CPU held back by Power saver gets fewer frames and less even frame times. Balanced removes the cap without raising idle power much.

In the Quiet and cool and Battery profiles it is the other way round: a High performance plan uses more power than needed at light load, which means more heat, more fan noise and less battery time. Balanced lowers power when there is little to do and still reaches the full clock under load [1].

## Evidence
Coming from Power saver, the effect depends on the processor and on how the plan was set up, so the app rates it as situational. Coming from High performance, the difference is power and heat, not frame rate: on mains power, Balanced lets the clock drop to a low minimum at light load, High performance keeps it at the top [2]. That is why the app gives it a gaming rating only while the PC uses Power saver.

## Trade-offs & risks
Higher power draw under load than Power saver. Compared with High performance, the processor clocks down at light load and has to clock up again when load arrives.

## When not to use it
For gaming it is not needed if you already use Balanced, High performance or a custom performance plan. That is why the app recommends it over High performance only in the Quiet and cool and Battery profiles.

## Sources
1. https://learn.microsoft.com/en-us/windows/win32/power/power-policy-settings
2. https://learn.microsoft.com/en-us/windows-server/administration/performance-tuning/hardware/power/power-performance-tuning
