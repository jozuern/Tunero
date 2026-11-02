# FPS-Limit unter der G-SYNC-Refresh-Rate

## Zusammenfassung
Setzt das globale NVIDIA-FPS-Limit 3 FPS unter die Refresh-Rate des G-SYNC-Monitors, damit Spiele, die schneller als der Monitor laufen, keine V-Sync-Verzögerung bekommen.

## So funktioniert es
NVIDIA speichert Treibereinstellungen in Profilen: einem globalen Profil und einem pro Spiel. Die App schreibt die dokumentierte Einstellung „Frame Rate Limiter“ (FRL_FPS) über NVIDIAs Schnittstelle für Treibereinstellungen in das globale Profil [1][2]. Spiele mit eigenem Wert in ihrem Profil behalten ihn. Rückgängig machen stellt den vorherigen Wert wieder her oder entfernt ihn, wenn das Profil keinen hatte.

## Warum es helfen kann
Mit G-SYNC und erzwungenem V-Sync stößt ein Spiel, das schneller als die Refresh-Rate läuft, an die G-SYNC-Obergrenze, und V-Sync übernimmt. Blur Busters (eine unabhängige Testseite) hat mit einer Hochgeschwindigkeitskamera gemessen, dass doppelt gepuffertes V-Sync dann 2 bis 6 Bilder Verzögerung bringt [3]. Ein Limit 2 FPS unter der Refresh-Rate verhinderte das bei jeder getesteten Refresh-Rate, 3 FPS darunter ist der empfohlene Abstand [4].

## Belege
Gemessen von Blur Busters 2017 mit einem Monitor mit G-SYNC-Modul unter Windows 10; das Prinzip (V-Sync-Pufferung an der Refresh-Obergrenze) ist unverändert. Dass die NVIDIA App diese Einstellung als „Max. Bildrate“ zeigt, ist auf einem echten PC noch nicht bestätigt, deshalb trägt die Behebung das Abzeichen „Noch nicht auf einem echten PC getestet“.

## Nachteile & Risiken
Das Limit gilt für jedes Spiel ohne eigenen Wert. Mit G-SYNC kostet das nichts Sichtbares, weil Bilder über der Refresh-Rate ohnehin nicht ohne Verzögerung gezeigt werden können. Ein Limiter im Spiel oder NVIDIA Reflex verzögert weniger; nutze sie, wo ein Spiel sie hat.

## Wann du es nicht nutzen solltest
Ohne G-SYNC, oder wenn du absichtlich ein anderes globales Limit nutzt.

## Quellen
1. https://archive.docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__drsapi.html
2. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
3. https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/4/
4. https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/5/
