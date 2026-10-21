# Num Lock on at the sign-in screen

## Summary
Turns Num Lock on at the sign-in screen, so a PIN or password can be typed on the numeric keypad right away.

## How it works
At the sign-in screen nobody is signed in yet, so Windows takes the keyboard lights from the settings it uses before sign-in, HKEY_USERS\.DEFAULT. Their InitialKeyboardIndicators value is 2147483648 by default, with Num Lock off; 2147483650 turns it on [1][2]. Microsoft does not document this value. A test on Windows 11 26H2 showed the effect: after a restart the numeric keypad typed into the password box with 2147483650 and not with the default, and not again after undo.

## Why it can help
You can type a PIN on the numeric keypad without pressing Num Lock first.

## Evidence
A convenience setting without effect on frame rate or latency.

## Trade-offs & risks
It takes effect at the next sign-in screen, after a restart or sign-out. With Fast Startup on, users report that Windows does not always read the value when the PC starts [2]. After you sign in, Num Lock follows your own account's setting.

## When not to use it
On a laptop whose letter keys double as a numeric keypad, Num Lock turns some letters into digits.

## Sources
1. https://winaero.com/enable-numlock-logon-screen-windows-10
2. https://learn.microsoft.com/en-us/answers/a/7567804
