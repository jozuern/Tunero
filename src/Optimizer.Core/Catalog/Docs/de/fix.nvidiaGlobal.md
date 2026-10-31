# Bremsende globale NVIDIA-Einstellungen zurücksetzen

## Zusammenfassung
Setzt die globalen NVIDIA-Einstellungen, die Spiele begrenzen (FPS-Limit, Energiemodus), auf den Treiberstandard zurück; mit G-SYNC kommt das Limit knapp unter die Refresh-Rate.

## So funktioniert es
NVIDIA speichert Treibereinstellungen in Profilen: einem globalen und einem pro Spiel. Die App entfernt die genannten Einstellungen über NVIDIAs Schnittstelle für Treibereinstellungen aus dem globalen Profil [1]. Spielprofile bleiben, wie sie sind. Rückgängig machen schreibt die vorherigen Werte zurück.

Ist G-SYNC an und V-Sync erzwungen, entfernt sie ein niedriges FPS-Limit nicht, sondern setzt es 3 FPS unter die Refresh-Rate: Ohne Limit bekäme ein Spiel, das schneller als der Monitor läuft, V-Sync-Verzögerung, und Blur Busters (eine unabhängige Testseite) fand 3 FPS unter der Refresh-Rate als sicherste Marge [4].

## Warum es helfen kann
Ein globales FPS-Limit weit unter der Refresh-Rate oder ein auf Minimum gestellter Energiemodus bremst jedes Spiel. Ohne sie läuft jedes Spiel so, wie der Treiber es vorsieht.

## Belege
Ein FPS-Limit begrenzt die Framerate absichtlich, und der minimale Energiemodus hält die Grafikkarte auf niedrigeren Takten. Entfernt man sie, gilt wieder das normale Verhalten. In der NVIDIA-Systemsteuerung ist das FPS-Limit standardmäßig aus [2], und die Header-Datei der Treibereinstellungen legt als Standard für den Energiemodus „Optimale Leistung“ fest [3].

## Nachteile & Risiken
Hast du das Limit bewusst gesetzt, etwa gegen Wärme oder Lautstärke, verlierst du das. Setze es dann pro Spiel.

## Wann du es nicht nutzen solltest
Wenn du bewusst ein globales FPS-Limit nutzt.

## Quellen
1. https://docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__drsapi.html
2. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
3. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
4. https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/5/
