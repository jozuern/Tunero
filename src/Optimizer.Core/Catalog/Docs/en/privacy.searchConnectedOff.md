# Windows Search: no web results and no location

## Summary
Sets three Windows Search policies against web search and location use. The one against web results is documented for Enterprise and Education only. Pro, Enterprise and Education.

## How it works
Three Windows Search policies Microsoft lists for limiting connections: "Do not allow web search" (DisableWebSearch = 1), "Don't search the web or display web results in Search" (ConnectedSearchUseWeb = 0) and "Allow search to use location" set to 0 [1][2]. Microsoft lists "Don't search the web or display web results in Search" for Enterprise and Education only, not for Pro [2]; on Pro, only the other two are documented to apply. To remove web results from Start search on Pro too, use the tweak "Web results in Start search off".

## Why it can help
On Enterprise and Education, what you type into Search stays on the PC. On all three editions, Search cannot use your location for results.

## Evidence
Documented Windows policies [1][2]. A privacy setting without effect on frame rate or latency.

## Trade-offs & risks
Search finds only apps, settings and files on this PC; for web results you open a browser.

## When not to use it
If you use web results in Windows Search.

## Sources
1. https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
2. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search
