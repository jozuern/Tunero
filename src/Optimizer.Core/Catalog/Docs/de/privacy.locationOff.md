# Standortzugriff aus

## Zusammenfassung
Schaltet die Standortdienste für den ganzen PC mit der Richtlinie „Standort deaktivieren“ ab. Die Einstellungen zeigen den Schalter dann aus und gesperrt. Für Pro, Enterprise und Education.

## So funktioniert es
Die App setzt die Microsoft-Richtlinie „Standort deaktivieren“ (DisableLocation = 1), die Microsoft nennt, um den Standort auf einem Gerät abzuschalten [2][4]. Windows und Apps bekommen dann keinen Gerätestandort mehr [1]. Unter Einstellungen > Datenschutz und Sicherheit > Standort stehen die Standortdienste und alle App-Berechtigungen auf Aus und lassen sich nicht ändern, solange die Richtlinie gesetzt ist. Ein Test unter Windows 11 26H2 zeigte das direkt nach dem Anwenden; nach dem Rückgängigmachen waren die Standortdienste wieder an. Den Einwilligungswert zu schreiben, den die Einstellungen speichern, wirkt unter Windows 11 26H2 nicht mehr: Windows hält den Schalter in einem eigenen Speicher und beachtet diesen Wert nicht. Home-Editionen sind ausgenommen, weil der Test unter Pro lief.

## Warum es helfen kann
Keine Standortabfragen im Hintergrund. Kein Effekt auf die Leistung.

## Belege
Eine Datenschutzeinstellung ohne Effekt auf die FPS.

## Nachteile & Risiken
Apps, die automatische Zeitzone und „Mein Gerät suchen“ verlieren den Gerätestandort [1]. Manche Funktionen, etwa das Wetter in der Taskleiste, können weiter deine IP-Adresse nutzen, und ein Notruf übermittelt deinen Standort trotzdem [1]. Der eigene WLAN-Band-Check der App zeigt danach „unbekannt“: Windows gibt WLAN-Details nur an Apps weiter, die den Standort nutzen dürfen [3].

## Wann du es nicht nutzen solltest
Lass den Standort an, wenn du Apps oder Funktionen nutzt, die ihn brauchen.

## Quellen
1. https://support.microsoft.com/en-us/windows/windows-location-service-and-privacy-3a8eee0a-5b0b-dc07-eede-2a5ca1c49088
2. https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
3. https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes
4. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-sensors
