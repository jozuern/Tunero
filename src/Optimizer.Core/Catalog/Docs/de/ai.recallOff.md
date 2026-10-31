# Recall entfernt

## Zusammenfassung
Macht Recall unverfügbar und entfernt seine Komponenten nach einem Neustart. Gespeicherte Momentaufnahmen werden gelöscht, Rückgängig holt sie nicht zurück. Pro, Enterprise und Education.

## So funktioniert es
Die Richtlinie AllowRecallEnablement auf 0 bedeutet, dass Recall nicht verfügbar ist: Die Recall-Komponente ist deaktiviert, ihre Dateien werden vom PC entfernt, und zuvor gespeicherte Momentaufnahmen werden gelöscht [1]. Das Entfernen braucht einen Neustart [1]. Microsoft führt die Richtlinie ab Windows 11 24H2 mit dem Update vom April 2025, für Pro, Enterprise und Education [1].

## Warum es helfen kann
Recall lässt sich nicht aus Versehen wieder einschalten, und seine Komponenten belegen keinen Platz mehr.

## Belege
Eine von Microsoft beschriebene Windows-Richtlinie [1]. Recall gibt es nur auf Copilot+ PCs; anderswo hat die Richtlinie nichts zu entfernen. Keine veröffentlichte Messung beim Spielen.

## Nachteile & Risiken
Gespeicherte Recall-Momentaufnahmen werden gelöscht, Rückgängig holt sie nicht zurück [1]. Nach dem Rückgängigmachen lässt sich Recall über die Windows-Features wieder hinzufügen. Wenn nur keine neuen Momentaufnahmen entstehen sollen, nutze stattdessen „Recall-Momentaufnahmen aus“.

## Wann du es nicht nutzen solltest
Wenn du Recall nutzt oder später nutzen willst.

## Quellen
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai
