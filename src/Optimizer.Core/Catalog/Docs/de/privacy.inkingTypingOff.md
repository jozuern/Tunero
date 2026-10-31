# Personalisierung von Freihand und Eingabe aus

## Zusammenfassung
Verhindert, dass Windows aus deinen Eingaben und deiner Handschrift lernt und diese Daten an Microsoft sendet. Löscht das bisherige Wörterbuch; Rückgängig holt es nicht zurück.

## So funktioniert es
Die Richtlinie „Automatisches Lernen deaktivieren“ verhindert, dass Windows getippten Text und Handschrift für dein persönliches Wörterbuch sammelt, und löscht die bisher gespeicherten Daten [3]. Die Richtlinie „Verbessern Sie die Freihand- und Tipperkennung“ wird abgeschaltet, damit keine Freihand- und Eingabedaten mehr an Microsoft gehen [1][2]. Für dein Konto schreibt die App die Werte, die die Einstellungen schreiben, wenn du unter Einstellungen > Datenschutz und Sicherheit > Personalisierung von Freihand und Eingabe das benutzerdefinierte Wörterbuch abschaltest: RestrictImplicitInkCollection und RestrictImplicitTextCollection, die Microsoft dokumentiert [2], dazu HarvestContacts und AcceptedPrivacyPolicy. Ohne diese Werte pro Benutzer bleibt der Schalter in den Einstellungen an, weil die Einstellungen AcceptedPrivacyPolicy lesen und nicht die Richtlinien. Außerdem schaltet die App die Eingabe-Einblicke ab. HarvestContacts, AcceptedPrivacyPolicy und den Wert für die Eingabe-Einblicke dokumentiert Microsoft nicht; unabhängige Anleitungen zeigen den ersten und den letzten [4][5][6][7], und in einem Test unter Windows 11 26H2 zeigten die Einstellungen den Schalter nach dem Anwenden aus und nach dem Rückgängigmachen wieder an.

## Warum es helfen kann
Weniger Daten verlassen diesen PC. Kein messbarer Einfluss auf die Leistung.

## Belege
Datenschutzeinstellung; sie ändert weder Framerate noch Latenz.

## Nachteile & Risiken
Textvorschläge und Handschrifterkennung lernen weniger von dir, und das bisher aufgebaute persönliche Wörterbuch wird gelöscht [3]. Rückgängig schaltet das Lernen wieder ein, holt das gelöschte Wörterbuch aber nicht zurück.

## Wann du es nicht nutzen solltest
Wenn du viel mit einem Stift schreibst.

## Quellen
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-textinput
2. https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
3. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-globalization
4. https://www.elevenforum.com/t/enable-or-disable-custom-inking-and-typing-dictionary-in-windows-11.5564/
5. https://geekrewind.com/how-to-turn-on-or-off-custom-inking-and-typing-dictionary-in-windows-11/
6. https://www.elevenforum.com/t/enable-or-disable-typing-insights-in-windows-11.7946/
7. https://winaero.com/enable-disable-typing-insights-windows-10/
