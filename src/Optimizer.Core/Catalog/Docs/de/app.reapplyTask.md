# Änderungen erneut anwenden, wenn Windows sie zurücksetzt

## Zusammenfassung
Zwei Minuten nach deiner Anmeldung startet Tunero kurz von selbst, wendet deine Änderungen erneut an, die ein Windows-Update zurückgesetzt hat, und beendet sich.

## So funktioniert es
Beim Einschalten legt Tunero für dein Konto eine geplante Aufgabe im Ordner Tunero der Aufgabenplanung an; schaltet ein anderer Administrator sie ein, bekommt er eine eigene. Sie startet bei deiner Anmeldung mit zwei Minuten Verzögerung [1][2], unter deinem Konto mit Administratorrechten („mit höchsten Privilegien ausführen“) [3], und braucht deshalb keine Bestätigung. Sie startet Tunero mit `--reapply`: Tunero prüft den PC wie die Übersicht, nimmt von den Änderungen, die dort als zurückgesetzt stehen und die „Erneut anwenden“ anbietet, die seit einem Windows-Update zurückgesetzten (die Windows-Version hat sich geändert, seit du sie angewendet hast oder Tunero sie zuletzt unverändert vorgefunden hat), wendet sie erneut an und beendet sich. Eine Änderung, die du selbst in Windows zurückgestellt hast, bleibt so, wie du sie gelassen hast, ebenso eine Einstellung, die zwei deiner Änderungen verschieden setzen (ein Tweak und die Seite Dienste & Aufgaben beim selben Dienst); die Übersicht zeigt sie dir zur Entscheidung. Experten-, startkritische und für Anti-Cheat heikle Änderungen werden so nie angewendet; sie bleiben auf der Seite Änderungen für deine Entscheidung. Sonst ändert sich nichts, und kein Tunero-Prozess läuft weiter. Die Aufgabe ist eine Änderung wie jede andere: Die Seite Änderungen führt sie, und Rückgängig entfernt sie.

Einrichten lässt sich die Aufgabe nur, wenn Tunero in einem Ordner liegt, den nur Administratoren ändern können (Programme oder eine Installation von winget oder Chocolatey für alle Benutzer): Sie startet die Datei ohne Nachfrage mit Administratorrechten, und eine Datei im Download-Ordner könnte jedes Programm austauschen, das du startest.

## Warum es helfen kann
Ein Funktionsupdate oder eine Reparatur von Windows kann Werte auf Windows-Standards zurücksetzen. Ohne die Aufgabe zeigt die Übersicht sie beim nächsten Öffnen von Tunero als zurückgesetzt; mit ihr sind sie ohne Besuch wieder da.

## Belege
Keine eigene Wirkung auf die Leistung: Sie erhält nur die Wirkung der Änderungen, die du schon gewählt hast. Der Lauf dauert nach der Anmeldung einige Sekunden, etwa so lange wie eine Prüfung.

## Nachteile & Risiken
Auch eine Änderung, die Windows absichtlich zurückgesetzt hat (ein neuer Standard, der ein Problem behebt, oder eine Einstellung, die eine Richtlinie des Administrators steuert), kommt zurück. Jeder Lauf, der etwas anwendet, fügt dem Änderungsprotokoll eine Zeile hinzu und legt bei eingeschaltetem Computerschutz einen Wiederherstellungspunkt an. Verschiebst oder löschst du die Datei, schlägt die Aufgabe fehl, bis du sie vom neuen Ort aus aus- und wieder einschaltest. Deinstallierst du Tunero über seine eigenen Einstellungen (Einstellungen > Deinstallieren), wird die Aufgabe entfernt.

## Wann du es nicht nutzen solltest
Wenn du sehen willst, was ein Update zurückgesetzt hat, bevor etwas erneut angewendet wird: Nutze dann „Erneut anwenden“ auf der Übersicht. Auf einem PC, den andere verwalten.

## Quellen
1. https://learn.microsoft.com/en-us/windows/win32/taskschd/logon-trigger-example--xml-
2. https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-delay-logontriggertype-element
3. https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-runlevel
