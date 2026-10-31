# Öffentliche DNS-Server

## Zusammenfassung
Stellt die IPv4-DNS-Server deiner verbundenen physischen Netzwerkadapter auf einen öffentlichen Anbieter. Ändert, wie schnell Namen aufgelöst werden, nicht deinen Ping in Spielen.

## So funktioniert es
Jede Verbindung beginnt mit dem Auflösen eines Namens (zum Beispiel des Login-Servers eines Spiels). Meist antwortet der DNS-Server des Routers oder Providers. Die App stellt die IPv4-DNS-Server jedes verbundenen physischen Adapters über die Windows-Netzwerkkonfiguration auf den gewählten Anbieter [1] und nutzt dafür die Adressen, die die Anbieter veröffentlichen [2][3][4][5][6]. Virtuelle Adapter, etwa von VPNs oder virtuellen Maschinen, ändert sie nicht, ein VPN behält also seinen eigenen DNS. IPv6-DNS-Server, die dein Router verteilt, bleiben eingetragen, Windows kann sie also weiter nutzen. Rückgängig machen stellt die vorherige Einstellung wieder her, auch „automatisch“ (DHCP). Adapter, die erst nach dem Start der App verbunden werden, ändert sie nicht.

## Warum es helfen kann
Ein langsamer oder unzuverlässiger DNS-Server verzögert jede neue Verbindung: Launcher, Shopseiten, Matchmaking. Der DNS-Test auf der Seite Grafik & DNS misst, welcher Server von deinem Anschluss aus am schnellsten antwortet.

## Belege
DNS dient nur dazu, einen Server zu finden. Ist das Spiel verbunden, spielt DNS keine Rolle mehr, Ping und Framerate ändern sich nicht. Deshalb ist die Wirkung mit 0 bewertet.

## Nachteile & Risiken
Die DNS-Server mancher Provider finden nahe Download-Server besser. Firmen- oder Schulnetze verlangen eventuell ihren eigenen DNS. Quad9 sperrt Domains, die es als schädlich einstuft [4]. Das trifft sehr selten auch eine harmlose Seite. Die Standardserver von AdGuard DNS blockieren Werbung und Tracker [6]: Manche Seiten oder Apps, die darauf angewiesen sind, funktionieren dann nicht vollständig.

## Wann du es nicht nutzen solltest
In Firmen- oder Schulnetzen oder wenn der DNS deines Providers im Test schnell ist.

## Quellen
1. https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/setdnsserversearchorder-method-in-class-win32-networkadapterconfiguration
2. https://developers.cloudflare.com/1.1.1.1/ip-addresses/
3. https://developers.google.com/speed/public-dns/docs/using
4. https://www.quad9.net/service/service-addresses-and-features/
5. https://www.opendns.com/setupguide/
6. https://adguard-dns.io/en/public-dns.html
