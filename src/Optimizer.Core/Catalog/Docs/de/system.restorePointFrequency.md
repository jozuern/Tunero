# Mehr als einen Wiederherstellungspunkt pro Tag erlauben

## Zusammenfassung
Intern: Damit kann die App vor Änderungen einen Wiederherstellungspunkt anlegen, auch wenn in den letzten 24 Stunden schon einer erstellt wurde.

## So funktioniert es
Windows überspringt neue Wiederherstellungspunkte innerhalb von 24 Stunden nach dem letzten. SystemRestorePointCreationFrequency = 0 hebt diese Grenze auf [1]. Die App setzt den Wert vor ihrer ersten Änderung in einer Sitzung selbst. In der Liste der Tweaks steht er nicht, die Seite Änderungen zeigt ihn aber als eigenen Eintrag, und „Alles rückgängig“ entfernt ihn wieder.

## Warum es helfen kann
Stellt sicher, dass es einen frischen Wiederherstellungspunkt gibt, bevor die App etwas ändert.

## Belege
Keine Leistungseinstellung.

## Nachteile & Risiken
Programme, die Wiederherstellungspunkte anlegen, können mehr davon anlegen. Die Größenbegrenzung des Computerschutzes gilt weiterhin.

## Wann du es nicht nutzen solltest
Es spricht nichts dagegen, solange du diese App nutzt.

## Quellen
1. https://learn.microsoft.com/en-us/windows/win32/sr/calling-srsetrestorepoint
