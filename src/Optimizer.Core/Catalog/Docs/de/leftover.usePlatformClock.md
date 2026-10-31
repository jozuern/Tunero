# Erzwungene Plattformuhr entfernen (useplatformclock)

## Zusammenfassung
Experte, Startkonfiguration: entfernt useplatformclock, eine Debug-Option aus älteren Anleitungen. Sie erzwingt einen langsameren Plattform-Timer. Danach wählt Windows den Timer wieder selbst.

## So funktioniert es
Die Startoption useplatformclock zwingt Windows, die Plattformuhr als Leistungszähler zu nutzen. Laut Microsoft ist sie nur zur Fehlersuche gedacht [1]. Die Plattformuhr ist der HPET oder der ACPI-PM-Timer. Normalerweise nutzt Windows den Zeitstempelzähler der CPU (TSC), wenn er geeignet ist [2]. Die App löscht die Option, nachdem sie die Startkonfiguration exportiert hat.

## Warum es helfen kann
Den TSC auszulesen dauert einige Dutzend bis einige Hundert CPU-Takte. Ein Plattform-Timer braucht etwa 0,8 bis 1,0 Mikrosekunden und einen Systemaufruf [2]. Spiele fragen die Zeit pro Frame oft ab, das summiert sich.

## Belege
Den Kostenunterschied dokumentiert Microsoft [2]. Wie stark sich das auf die FPS auswirkt, hängt davon ab, wie oft ein Spiel die Zeit abfragt.

## Nachteile & Risiken
Eine Änderung der Startkonfiguration. Ist BitLocker aktiv, halte deinen Wiederherstellungsschlüssel bereit: Laut Microsoft muss BitLocker vor Änderungen an Startoptionen eventuell angehalten werden [1]. Startet der PC nicht richtig, hilft die Systemwiederherstellung nicht, denn sie stellt die Startkonfiguration nicht wieder her. Öffne die Wiederherstellungsumgebung (Umschalt + Neu starten > Problembehandlung > Erweiterte Optionen > Eingabeaufforderung) und führe `bcdedit /set {default} useplatformclock yes` aus, um die Einstellung wiederherzustellen [1][3]. Die App exportiert vor der Änderung außerdem die ganze Startkonfiguration in ihren Datenordner; `bcdedit /import <Datei>` stellt sie von dort wieder her [3].

## Wann du es nicht nutzen solltest
Nichts zu tun, wenn die Option nicht gesetzt ist. Startoptionen nur mit direktem Zugriff auf den PC ändern.

## Quellen
1. https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set
2. https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps
3. https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/bcdedit-command-line-options
