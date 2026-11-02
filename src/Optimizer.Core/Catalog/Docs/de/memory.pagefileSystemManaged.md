# Auslagerungsdatei von Windows verwalten lassen

## Zusammenfassung
Stellt „Auslagerungsdateigröße für alle Laufwerke automatisch verwalten“ wieder her. Behebt Abstürze und Speicherfehler durch eine abgeschaltete oder zu kleine Auslagerungsdatei.

## So funktioniert es
Das Commit-Limit, also wie viel Speicher Programme insgesamt zusichern lassen können, ist RAM plus alle Auslagerungsdateien [1]. Alte Anleitungen empfehlen, die Auslagerungsdatei abzuschalten oder eine kleine feste Größe zu setzen. Die App setzt den Wert PagingFiles auf ?:\pagefile.sys. Diesen Wert schreibt der Windows-Dialog, wenn „Auslagerungsdateigröße für alle Laufwerke automatisch verwalten“ gesetzt ist. Microsoft dokumentiert den Wert selbst nicht. Windows bestimmt die Größe dann selbst, bis zum Dreifachen des RAMs oder 4 GB, je nachdem, was größer ist, und höchstens ein Achtel des Laufwerks [2]. Wirkt nach einem Neustart.

## Warum es helfen kann
Erreichen Programme das Commit-Limit, kann Windows einfrieren, abstürzen oder Speicherfehler melden [1]. Eine verwaltete Auslagerungsdatei wächst mit, wenn die zugesicherte Menge nahe ans Limit kommt [2].

## Belege
Standardmäßig verwaltet Windows die Auslagerungsdatei selbst [2]. Eine Auslagerungsdatei oder eigene Abbilddatei wird außerdem gebraucht, um nach einem Absturz ein Speicherabbild zu speichern [2].

## Nachteile & Risiken
Belegt Platz auf dem Systemlaufwerk, bis zu den genannten Grenzen.

## Wann du es nicht nutzen solltest
Behalte eine eigene Größe nur, wenn du sie bewusst gesetzt hast und den Spitzenbedarf deiner Anwendungen kennst. Nutzt du auf Laufwerk C: bereits „Größe wird vom System verwaltet“, ändert sich praktisch nichts, auch wenn die App die Einstellung als nicht angewendet zeigt.

## Quellen
1. https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file
2. https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows
