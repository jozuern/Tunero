# Keine automatische Installation vorgeschlagener Apps

## Zusammenfassung
Verhindert, dass Windows vorgeschlagene und beworbene Apps für dein Konto installiert, auf Home und Pro, wo die Richtlinie für Verbraucherfunktionen nicht gilt. Nicht dokumentierte Werte; Vorschau.

## So funktioniert es
Windows installiert nach der Einrichtung von sich aus einige vorgeschlagene Apps; Microsoft nennt das Verbraucherfunktionen [1]. Die Richtlinie, die sie abschaltet, gilt nur für Enterprise und Education [1]. Auf Home und Pro entscheiden die Werte SilentInstalledAppsEnabled, PreinstalledAppsEnabled, PreinstalledAppsEverEnabled und OemPreinstalledAppsEnabled unter ContentDeliveryManager für dein Konto, und Tools wie Win11Debloat setzen sie auf 0 [2]. Microsoft dokumentiert diese Werte nicht, und noch kein Test hat die Wirkung gezeigt, daher bleibt die Option eine Vorschau.

## Warum es helfen kann
Weniger Apps, um die du nicht gebeten hast, und keine Downloads dafür im Hintergrund.

## Belege
Noch nicht nachgewiesen. Kein Einfluss auf Framerate oder Latenz.

## Nachteile & Risiken
Bereits installierte Apps bleiben; entferne sie auf der Seite Apps entfernen. Auf Enterprise und Education ist „Verbraucherfunktionen aus“ der dokumentierte Weg.

## Wann du es nicht nutzen solltest
Wenn Windows vorgeschlagene Apps installieren soll.

## Quellen
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
2. https://github.com/Raphire/Win11Debloat/blob/master/Regfiles/Disable_Windows_Suggestions.reg
