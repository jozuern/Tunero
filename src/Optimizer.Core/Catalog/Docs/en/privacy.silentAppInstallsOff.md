# No automatic installs of suggested apps

## Summary
Stops Windows from installing suggested and promoted apps for your account on Home and Pro, where the consumer features policy does not apply. Values Microsoft does not document; Preview.

## How it works
Windows installs some suggested apps by itself after setup; Microsoft calls these consumer experiences [1]. The policy that turns them off applies only to Enterprise and Education [1]. On Home and Pro, the values SilentInstalledAppsEnabled, PreinstalledAppsEnabled, PreinstalledAppsEverEnabled and OemPreinstalledAppsEnabled under ContentDeliveryManager decide this for your account, and tools such as Win11Debloat set them to 0 [2]. Microsoft does not document these values, and no test has shown the effect yet, so the tweak stays a Preview.

## Why it can help
Fewer apps you did not ask for, and no downloads for them in the background.

## Evidence
Not proven yet. No effect on frame rate or latency.

## Trade-offs & risks
Apps that are already installed stay; remove them on the Remove apps page. On Enterprise and Education, "Consumer features off" is the documented way.

## When not to use it
If you want Windows to install suggested apps.

## Sources
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
2. https://github.com/Raphire/Win11Debloat/blob/master/Regfiles/Disable_Windows_Suggestions.reg
