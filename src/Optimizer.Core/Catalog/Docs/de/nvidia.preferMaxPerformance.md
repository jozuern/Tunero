# NVIDIA-Energiemodus: maximale Leistung bevorzugen (global)

## Zusammenfassung
Hält die Grafikkarte bei den meisten 3D-Anwendungen auf maximaler Leistung. Kann Frametimes in leichten Spielen glätten, erhöht den Stromverbrauch. Pro Spiel ist meist besser.

## So funktioniert es
Standardmäßig senkt der Treiber den Grafiktakt bei geringer Last. „Maximale Leistung bevorzugen“ im globalen Profil weist ihn an, die GPU bei den meisten 3D-Anwendungen mit maximaler Leistung zu betreiben [1]. Die App schreibt den Wert aus NVIDIAs Header-Datei der Treibereinstellungen über die Schnittstelle für Treibereinstellungen [2].

## Warum es helfen kann
In leichten oder älteren Spielen kann die Karte zwischen den Frames heruntertakten und braucht Zeit zum Hochtakten. Das kann ungleichmäßige Frametimes verursachen.

## Belege
Die Wirkung hängt von der Situation ab; in anspruchsvollen Spielen läuft die Karte ohnehin mit vollem Takt, und es ändert sich nichts.

## Nachteile & Risiken
Mehr Stromverbrauch, Wärme und Lüftergeräusch, auch bei Browsern oder Launchern mit 3D-Beschleunigung.

## Wann du es nicht nutzen solltest
Nutze lieber die Variante pro Spiel auf der Seite Grafik & DNS, die nur die Spiele betrifft, die du auswählst.

## Quellen
1. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
2. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
