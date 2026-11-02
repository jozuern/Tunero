# Dienst beim Start

## Zusammenfassung
Lässt diesen Dienst bei Bedarf (Manuell) statt mit Windows starten. Einen Windows-Dienst nur, wo Tunero ihn erklärt.

## So funktioniert es
Der Starttyp wird im Dienststeuerungs-Manager auf Manuell gestellt [1]; der Dienst wird nicht deaktiviert, sein Programm kann ihn weiterhin starten. Rückgängig machen stellt den vorherigen Starttyp wieder her.

## Warum es helfen kann
Programme, die mit Windows starten, machen die Anmeldung langsamer und belegen RAM und manchmal CPU-Zeit im Hintergrund.

## Belege
Die Anmeldung wird schneller; der Effekt auf die Framerate hängt davon ab, was das Programm im Hintergrund tut.

## Nachteile & Risiken
Funktionen des Programms, die den Dienst direkt nach dem Start brauchen (zum Beispiel Updateprüfung oder Geräteerkennung), starten eventuell später oder gar nicht.

## Wann du es nicht nutzen solltest
Bei Anti-Cheat-, Sicherheits-, VPN- und Treiberdiensten.

## Quellen
1. https://learn.microsoft.com/en-us/windows/win32/services/service-startup
