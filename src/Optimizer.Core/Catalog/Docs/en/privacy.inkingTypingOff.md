# Inking and typing personalization off

## Summary
Stops Windows from learning from your typing and handwriting and from sending that data to Microsoft. Deletes the dictionary built so far; Undo does not bring it back.

## How it works
The policy "Turn off automatic learning" stops Windows from collecting typed text and handwriting for your personal dictionary and deletes what was stored [3]. The policy "Improve inking and typing recognition" is turned off, so inking and typing data is no longer sent to Microsoft [1][2]. For your account, the app writes the values Settings writes when you turn off "Custom inking and typing dictionary" under Settings > Privacy & security > Inking & typing personalization: RestrictImplicitInkCollection and RestrictImplicitTextCollection, which Microsoft documents [2], plus HarvestContacts and AcceptedPrivacyPolicy. Without these per-user values the switch in Settings stays on, because Settings reads AcceptedPrivacyPolicy and not the policies. It also turns off typing insights. Microsoft does not document HarvestContacts, AcceptedPrivacyPolicy and the typing insights value; independent tutorials show the first and the last [4][5][6][7], and in a test on Windows 11 26H2 Settings showed the switch off after apply and on again after undo.

## Why it can help
Less data leaves this PC. No measurable effect on performance.

## Evidence
Privacy setting; it does not change frame rate or latency.

## Trade-offs & risks
Text suggestions and handwriting recognition learn less from you, and the personal dictionary Windows built so far is deleted [3]. Undo turns learning back on, but it does not bring the deleted dictionary back.

## When not to use it
If you rely on handwriting with a pen.

## Sources
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-textinput
2. https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
3. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-globalization
4. https://www.elevenforum.com/t/enable-or-disable-custom-inking-and-typing-dictionary-in-windows-11.5564/
5. https://geekrewind.com/how-to-turn-on-or-off-custom-inking-and-typing-dictionary-in-windows-11/
6. https://www.elevenforum.com/t/enable-or-disable-typing-insights-in-windows-11.7946/
7. https://winaero.com/enable-disable-typing-insights-windows-10/
