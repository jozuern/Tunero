# OneDrive-Synchronisierung aus

## Zusammenfassung
Beendet die Synchronisierung von OneDrive und blendet es im Explorer aus, ohne es zu deinstallieren. Nur online gespeicherte Dateien sind nicht verfügbar. Pro, Enterprise und Education.

## So funktioniert es
Die Richtlinie „Verwendung von OneDrive für die Dateispeicherung verhindern“ (DisableFileSyncNGSC = 1) bewirkt: kein Zugriff auf OneDrive aus der OneDrive-App und der Dateiauswahl, Store-Apps erreichen es nicht, OneDrive fehlt im Navigationsbereich des Explorers, Dateien werden nicht mehr synchronisiert, und Kamera-Uploads enden [1].

## Warum es helfen kann
Kein Synchronisierungsverkehr und keine OneDrive-Prozesse im Hintergrund, auch beim Spielen, während OneDrive für später installiert bleibt.

## Belege
Eine von Microsoft beschriebene Windows-Richtlinie [1]. Wie viel es spart, hängt davon ab, wie viel du synchronisierst.

## Nachteile & Risiken
Dateien, die nur in der Cloud liegen, lassen sich auf diesem PC nicht öffnen, und Änderungen werden nicht synchronisiert. Sichert OneDrive deine Ordner Desktop, Dokumente oder Bilder, bleiben diese Ordner im OneDrive-Ordner: Neue Dateien darin werden nicht mehr gesichert, und Dateien darin, die nur online liegen, lassen sich nicht öffnen. Die Einstellung gilt für alle Konten auf diesem PC. Rückgängig schaltet die Synchronisierung wieder ein. Um OneDrive ganz zu entfernen, nutze die Seite Apps entfernen.

## Wann du es nicht nutzen solltest
Wenn du OneDrive nutzt oder Dateien brauchst, die nur online liegen.

## Quellen
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system
