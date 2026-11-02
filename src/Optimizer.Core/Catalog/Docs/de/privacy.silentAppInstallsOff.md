# Keine automatische Installation vorgeschlagener Apps

## Zusammenfassung
Verhindert, dass Windows vorgeschlagene und beworbene Apps für dein Konto installiert, auf Home und Pro, wo die Richtlinie für Verbraucherfunktionen nicht gilt. Nicht dokumentierter Wert; Vorschau.

## So funktioniert es
Windows installiert nach der Einrichtung von sich aus einige vorgeschlagene Apps; Microsoft nennt das Verbraucherfunktionen [1]. Die Richtlinie, die sie abschaltet, gilt nur für Enterprise und Education [1]. Auf Home und Pro entscheidet der Wert SilentInstalledAppsEnabled unter ContentDeliveryManager darüber für dein Konto, und Tools wie Win11Debloat setzen ihn auf 0 [2]. Microsoft dokumentiert diesen Wert nicht, und noch kein Test hat die Wirkung gezeigt, daher bleibt die Option eine Vorschau. PreinstalledAppsEnabled, PreinstalledAppsEverEnabled und OemPreinstalledAppsEnabled, die manche Tools zusätzlich setzen, lässt die App weg: Keine Quelle, die wir prüfen konnten, zeigt, was sie unter Windows 11 bewirken.

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
