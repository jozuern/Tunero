# Zwischenablageverlauf an

## Zusammenfassung
Schaltet den Zwischenablageverlauf ein: Windows + V zeigt, was du vorher kopiert hast, und du kannst jeden Eintrag erneut einfügen.

## So funktioniert es
Die App setzt EnableClipboardHistory auf 1, den Benutzerwert hinter Einstellungen > System > Zwischenablage > Zwischenablageverlauf [1]. Microsoft beschreibt den Schalter [1], der Registry-Wert dahinter ist aber nicht dokumentiert; die App schreibt denselben Wert, den Windows beim Umlegen des Schalters speichert. Solange kein Test auf einer echten Windows-Installation die Wirkung bestätigt, trägt die Option das Etikett „Noch nicht auf einem echten PC getestet“.

## Warum es helfen kann
Wer mehrere Dinge zwischen Dokumenten kopiert, muss nicht mehr ständig das Fenster wechseln. Angeheftete Einträge bleiben für Texte verfügbar, die du oft einfügst.

## Belege
Eine Standardfunktion von Windows [1]. Der Gewinn ist Komfort, nicht Geschwindigkeit.

## Nachteile & Risiken
Der Verlauf behält, was du kopierst, auch kopierte Passwörter aus Texten. Nicht angeheftete Einträge werden beim Neustart gelöscht. Die Synchronisierung auf andere Geräte ist eine eigene Einstellung, die App ändert sie nicht. Nicht kombinierbar mit „Zwischenablageverlauf aus“, das den Verlauf per Richtlinie sperrt [2].

## Wann du es nicht nutzen solltest
Auf einem gemeinsam genutzten PC oder wenn du oft vertrauliche Daten kopierst und sie nicht in einer Liste behalten willst.

## Quellen
1. https://support.microsoft.com/en-us/windows/clipboard-in-windows-c436501e-985d-1c8d-97ea-fe46ddf338c6
2. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience
