# Game-Bar-Aufnahmen aus

## Zusammenfassung
Schaltet die Aufnahmen der Xbox Game Bar ab, auch „Aufzeichnen, was passiert ist“, das die letzten Spielminuten ständig im Hintergrund aufnimmt.

## So funktioniert es
Ist die Hintergrundaufnahme an, kodiert Windows die letzten Spielminuten laufend mit dem Video-Encoder der GPU in einen Puffer. Die App schaltet Game DVR, App-Aufnahme und Hintergrundaufnahme für dein Benutzerkonto ab, mit denselben Werten wie WinUtil [2]. Microsoft dokumentiert diese Werte pro Benutzer nicht; unabhängige Anleitungen zeigen, dass sie die Game-Bar-Aufnahme [3][4] und „Aufzeichnen, was passiert ist“ [5] abschalten, und Microsofts Aufnahme-Schnittstelle nutzt dieselben Namen [6]. Die dokumentierte Richtlinie „AllowGameDVR“ schaltet die Aufnahme für den ganzen PC ab [1]; auf Pro, Enterprise und Education setzt der Tweak „Game-Bar-Aufnahme per Richtlinie aus“ diese Richtlinie. Die Game Bar selbst bleibt installiert.

## Warum es helfen kann
Die Daueraufnahme belegt Encoder-Zeit der GPU, RAM und Schreibzugriffe. Ohne sie fällt diese Last beim Spielen weg.

## Belege
Ist die Hintergrundaufnahme aus, wie meist üblich, ändert sich nichts Messbares. Ist sie an, arbeitet der Video-Encoder der GPU während des Spielens ständig; eine veröffentlichte Messung der Kosten haben wir nicht gefunden.

## Nachteile & Risiken
Du verlierst Game-Bar-Clips und die Tastenkombination „Das aufzeichnen“. Aufnahmen mit NVIDIA ShadowPlay oder OBS sind nicht betroffen.

## Wann du es nicht nutzen solltest
Behalte die Funktion, wenn du Game-Bar-Clips nutzt.

## Quellen
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-applicationmanagement
2. https://github.com/ChrisTitusTech/winutil
3. https://www.tenforums.com/tutorials/8637-game-bar-turn-off-windows-10-a.html
4. https://www.thewindowsclub.com/enable-disable-game-dvr-windows-10
5. https://www.elevenforum.com/t/enable-or-disable-record-what-happened-for-gaming-captures-in-windows-11.17900/
6. https://learn.microsoft.com/en-us/uwp/api/windows.media.capture.appcapturesettings
