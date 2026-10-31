# Public DNS servers

## Summary
Sets the IPv4 DNS servers of your connected physical network adapters to a public resolver. Changes how fast names are looked up, not your ping in games.

## How it works
Every connection starts by looking up a name (for example a game's login server). Usually the router's or provider's DNS server answers. The app sets the IPv4 DNS servers of each connected physical adapter to the chosen provider through Windows' network configuration [1], using the addresses the providers publish [2][3][4][5][6]. Virtual adapters, for example those of VPNs or virtual machines, are not changed, so a VPN keeps its own DNS. IPv6 DNS servers that your router hands out stay configured, so Windows can still use them. Undo restores the previous setting, including "automatic" (DHCP). Adapters connected after the app started are not changed.

## Why it can help
A slow or unreliable DNS server delays every new connection: launchers, store pages, matchmaking. The DNS benchmark on the Graphics & DNS page measures which server answers fastest from your connection.

## Evidence
DNS is only used to find a server. Once the game is connected, DNS plays no role, so ping and frame rate do not change. That is why the impact is 0.

## Trade-offs & risks
Some providers' DNS servers find nearby download servers better. Company or school networks may require their own DNS. Quad9 blocks domains it rates as malicious [4], which very rarely affects a legitimate site. AdGuard DNS's default servers block ads and trackers [6]: some sites or apps that rely on them may not work fully.

## When not to use it
In company or school networks, or if your provider's DNS is fast in the benchmark.

## Sources
1. https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/setdnsserversearchorder-method-in-class-win32-networkadapterconfiguration
2. https://developers.cloudflare.com/1.1.1.1/ip-addresses/
3. https://developers.google.com/speed/public-dns/docs/using
4. https://www.quad9.net/service/service-addresses-and-features/
5. https://www.opendns.com/setupguide/
6. https://adguard-dns.io/en/public-dns.html
