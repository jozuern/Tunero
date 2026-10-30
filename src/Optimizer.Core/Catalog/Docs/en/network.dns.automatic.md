# DNS servers: automatic (from the router)

## Summary
Sets the IPv4 DNS servers of your connected physical network adapters back to automatic, so they come from the router (DHCP) again. Undoes DNS servers set by hand or by another tool.

## How it works
Through Windows' network configuration the app clears the DNS server list of each connected physical adapter, which means "obtain DNS server address automatically" [1]. Virtual adapters, for example those of VPNs, are not changed, and neither are adapters with a fixed IP address (DHCP off): no router hands them DNS servers, so an empty list would leave them without any. Undo restores the servers that were set before.

## Why it can help
After trying other DNS servers, this is the quickest way back to the default setting, also when the servers were set by another program.

## Evidence
A documented Windows setting [1]. DNS only finds servers; ping and frame rate do not change.

## Trade-offs & risks
Your router or provider decides which DNS servers you use again.

## When not to use it
If you set DNS servers on purpose, for example for a filter or in a company network.

## Sources
1. https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/setdnsserversearchorder-method-in-class-win32-networkadapterconfiguration
