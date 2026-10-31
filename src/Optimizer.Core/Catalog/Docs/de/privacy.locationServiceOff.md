# Ortungsdienst für alles aus

## Zusammenfassung
Schaltet den Ortungsdienst für alle Apps, die Suche und Windows aus und sperrt die Einstellungen. Älterer Eintrag mit derselben Richtlinie wie „Standortzugriff aus“. Pro, Enterprise und Education.

## So funktioniert es
Die Richtlinie „Standort deaktivieren“ (DisableLocation = 1) schaltet den Standort fest aus: Alle Standort-Datenschutzeinstellungen sind aus und ausgegraut, und keine App darf den Ortungsdienst nutzen, auch die Suche nicht [1][2]. Gibt die Richtlinie die Steuerung an die Benutzer zurück, bekommt jede App ihre frühere Einstellung wieder [1].

## Warum es helfen kann
Keine App und keine Windows-Funktion kann lesen, wo der PC steht, und die Einstellungen können das nicht übergehen. Die Option „Standortzugriff aus“ setzt inzwischen dieselbe Richtlinie; dieser Eintrag bleibt, damit du früher damit gemachte Änderungen rückgängig machen kannst.

## Belege
Eine von Microsoft beschriebene Windows-Richtlinie [1][2]. Eine Datenschutzeinstellung ohne Einfluss auf Framerate oder Latenz.

## Nachteile & Risiken
Apps und Windows-Funktionen, die deinen Standort nutzen, etwa Karten, Wetter und Mein Gerät suchen, bekommen ihn nicht mehr. Rückgängig gibt die Steuerung an die Einstellungen zurück [1].

## Wann du es nicht nutzen solltest
Wenn du den Standort in Karten, Wetter oder Mein Gerät suchen nutzt.

## Quellen
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system
2. https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
