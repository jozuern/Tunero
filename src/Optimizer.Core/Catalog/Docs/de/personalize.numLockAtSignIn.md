# Num Lock auf dem Anmeldebildschirm an

## Zusammenfassung
Schaltet Num Lock auf dem Anmeldebildschirm ein, damit du PIN oder Kennwort gleich über den Ziffernblock eingeben kannst.

## So funktioniert es
Auf dem Anmeldebildschirm ist noch niemand angemeldet, deshalb nimmt Windows die Tastatur-LEDs aus den Einstellungen für die Zeit vor der Anmeldung, HKEY_USERS\.DEFAULT. Deren Wert InitialKeyboardIndicators steht ab Werk auf 2147483648, Num Lock ist dann aus; 2147483650 schaltet es ein [1][2]. Dieser Wert ist von Microsoft nicht dokumentiert. Ein Test unter Windows 11 26H2 hat die Wirkung gezeigt: Nach einem Neustart tippte der Ziffernblock mit 2147483650 ins Kennwortfeld, mit dem Standardwert nicht und nach dem Rückgängigmachen auch nicht mehr.

## Warum es helfen kann
Du kannst eine PIN über den Ziffernblock eintippen, ohne vorher Num Lock zu drücken.

## Belege
Eine Komfort-Einstellung ohne Einfluss auf Framerate oder Latenz.

## Nachteile & Risiken
Es wirkt ab dem nächsten Anmeldebildschirm, also nach einem Neustart oder Abmelden. Ist der Schnellstart an, berichten Nutzer, dass Windows den Wert beim Einschalten nicht immer liest [2]. Nach der Anmeldung richtet sich Num Lock nach der Einstellung deines eigenen Kontos.

## Wann du es nicht nutzen solltest
Auf einem Laptop, dessen Buchstabentasten zugleich ein Ziffernblock sind: Mit Num Lock werden dort einige Buchstaben zu Ziffern.

## Quellen
1. https://winaero.com/enable-numlock-logon-screen-windows-10
2. https://learn.microsoft.com/en-us/answers/a/7567804
