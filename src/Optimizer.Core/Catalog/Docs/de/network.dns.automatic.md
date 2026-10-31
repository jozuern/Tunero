# DNS-Server: automatisch (vom Router)

## Zusammenfassung
Stellt die IPv4-DNS-Server deiner physischen Netzwerkadapter wieder auf automatisch (vom Router per DHCP). Hebt DNS-Server auf, die von Hand oder von einem anderen Tool gesetzt wurden.

## So funktioniert es
Über die Windows-Netzwerkkonfiguration leert die App die DNS-Serverliste jedes verbundenen physischen Adapters, das bedeutet „DNS-Serveradresse automatisch beziehen“ [1]. Virtuelle Adapter, etwa von VPNs, ändert sie nicht, ebenso wenig Adapter mit fester IP-Adresse (DHCP aus): Solchen Adaptern teilt kein Router DNS-Server zu, mit leerer Liste hätten sie keinen mehr. Rückgängig stellt die vorher gesetzten Server wieder her.

## Warum es helfen kann
Nach dem Ausprobieren anderer DNS-Server ist das der schnellste Weg zurück zur Standardeinstellung, auch wenn ein anderes Programm die Server gesetzt hat.

## Belege
Eine von Microsoft beschriebene Windows-Einstellung [1]. DNS findet nur Server; Ping und Framerate ändern sich nicht.

## Nachteile & Risiken
Dein Router oder Provider bestimmt wieder, welche DNS-Server du nutzt.

## Wann du es nicht nutzen solltest
Wenn du DNS-Server mit Absicht gesetzt hast, etwa für einen Filter oder in einem Firmennetz.

## Quellen
1. https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/setdnsserversearchorder-method-in-class-win32-networkadapterconfiguration
