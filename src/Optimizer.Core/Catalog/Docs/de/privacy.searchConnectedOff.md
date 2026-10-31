# Windows-Suche: keine Webergebnisse und kein Standort

## Zusammenfassung
Setzt drei Richtlinien der Windows-Suche gegen Websuche und Standortnutzung. Die gegen Webergebnisse ist nur für Enterprise und Education dokumentiert. Pro, Enterprise und Education.

## So funktioniert es
Drei Richtlinien der Windows-Suche, die Microsoft zum Begrenzen von Verbindungen nennt: „Websuche nicht zulassen“ (DisableWebSearch = 1), „Keine Websuche durchführen und keine Webergebnisse anzeigen“ (ConnectedSearchUseWeb = 0) und „Suche darf Standort verwenden“ auf 0 [1][2]. „Keine Websuche durchführen und keine Webergebnisse anzeigen“ führt Microsoft nur für Enterprise und Education, nicht für Pro [2]; unter Pro sind nur die anderen beiden als wirksam dokumentiert. Um Webergebnisse auch unter Pro aus der Startsuche zu entfernen, nutze die Option „Webergebnisse in der Startsuche aus“.

## Warum es helfen kann
Unter Enterprise und Education bleibt, was du in die Suche tippst, auf dem PC. In allen drei Editionen kann die Suche deinen Standort nicht für Ergebnisse nutzen.

## Belege
Von Microsoft beschriebene Windows-Richtlinien [1][2]. Eine Datenschutzeinstellung ohne Einfluss auf Framerate oder Latenz.

## Nachteile & Risiken
Die Suche findet nur Apps, Einstellungen und Dateien auf diesem PC; für Webergebnisse öffnest du einen Browser.

## Wann du es nicht nutzen solltest
Wenn du Webergebnisse in der Windows-Suche nutzt.

## Quellen
1. https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
2. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search
