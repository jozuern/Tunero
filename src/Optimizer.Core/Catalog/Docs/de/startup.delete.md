# Autostart-Eintrag löschen

## Zusammenfassung
Löscht diesen Autostart-Eintrag (Registry-Wert, Datei im Autostart-Ordner oder Aufgabe eines Programms) wie Autoruns. Tunero sichert ihn vorher, die Seite Änderungen stellt ihn wieder her.

## So funktioniert es
Windows führt die Befehlszeilen in den Run-Schlüsseln bei jeder Anmeldung aus, die in den RunOnce-Schlüsseln einmal [1]. Ein Run-Schlüssel aus einer Gruppenrichtlinie und der Wert AutoRun des Command Processor (läuft bei jedem Start von cmd.exe) funktionieren genauso, ebenso die Dateien in den Autostart-Ordnern und die geplanten Aufgaben, die andere Programme anlegen [2]. Ausschalten behält den Eintrag und markiert ihn als deaktiviert; Löschen entfernt ihn, so wie Autoruns [2]. Vorher speichert Tunero, was es löscht: einen Registry-Wert mit seinem Typ, eine Datei im Autostart-Ordner mit ihrem Inhalt, eine Aufgabe als das XML, das die Aufgabenplanung exportiert. Rückgängig schreibt den Wert oder die Datei zurück oder legt die Aufgabe aus ihrem XML unter demselben Namen und Konto wieder an [3]. Eine Aufgabe mit gespeichertem Kennwort wird nicht angeboten, weil sie sich nur mit diesem Kennwort wieder anlegen ließe [3].

## Warum es helfen kann
Ein Eintrag, dessen Programm deinstalliert wurde, bleibt in der Liste und zeigt auf eine Datei, die es nicht mehr gibt; Löschen räumt die Liste auf. RunOnce-, Richtlinien-Run- und AutoRun-Einträge lassen sich auf dieser Seite nicht ausschalten, Löschen ist hier also der einzige Weg, sie zu stoppen.

## Belege
Nichts Messbares: Ein Eintrag, der auf eine fehlende Datei zeigt, kostet bei der Anmeldung fast nichts. Bei Einträgen, die noch ein Programm starten, ist der Effekt derselbe wie beim Ausschalten.

## Nachteile & Risiken
Das Programm startet nicht mehr von selbst und legt den Eintrag oder die Aufgabe beim nächsten Start oder Update vielleicht wieder an. Ein RunOnce-Eintrag, den ein Installer für die nächste Anmeldung hinterlassen hat (um ein Update abzuschließen), läuft nicht; stelle ihn auf der Seite Änderungen wieder her, wenn das Programm danach Probleme macht. Fehlt die Update-Aufgabe eines Programms, aktualisiert es sich nicht mehr selbst.

## Wann du es nicht nutzen solltest
Bei Einträgen, die du nur pausieren willst: Schalte sie stattdessen aus, dann bleiben sie in der Liste. Bei RunOnce-Einträgen eines Installers, den du gerade ausgeführt hast: Lass ihn zuerst mit einem Neustart fertig werden. Bei der Update-Aufgabe eines Programms, das du behältst: Schalte sie lieber aus oder lass sie, statt sie zu löschen.

## Quellen
1. https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
2. https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns
3. https://learn.microsoft.com/en-us/windows/win32/taskschd/taskfolder-registertask
