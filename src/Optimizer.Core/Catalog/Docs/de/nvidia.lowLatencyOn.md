# NVIDIA-Modus für niedrige Latenz: Ein

## Zusammenfassung
Begrenzt die Warteschlange auf ein Bild, das der Prozessor für die Grafikkarte vorbereitet. Senkt den Input-Lag in GPU-limitierten DirectX-9- und -11-Spielen.

## So funktioniert es
Normalerweise lässt der Treiber den Prozessor einige Bilder vorausplanen, damit die Grafikkarte nie wartet. „Modus für niedrige Latenz: Ein“ in der NVIDIA Systemsteuerung begrenzt die Warteschlange auf ein Bild, wie früher „Maximale Anzahl vorgerenderter Bilder“ = 1 [1][2]. Die App schreibt diesen Wert über NVIDIAs Schnittstelle für Treibereinstellungen ins globale Profil; die Einstellung steht in NVIDIAs öffentlicher Header-Datei der Treibereinstellungen [4]. DirectX-12- und Vulkan-Spiele steuern ihre Warteschlange selbst und sind nicht betroffen [1].

## Warum es helfen kann
Ist die Grafikkarte der Engpass, verzögert jedes Bild in der Warteschlange den Weg von deiner Eingabe zum Bild um ein Bild. Mit einem statt mehreren Bildern sinkt die Verzögerung höchstens um die Dauer der weggefallenen Bilder.

## Belege
Laut NVIDIA wirken die Latenzmodi am stärksten, wenn ein Spiel bei etwa 60 bis 100 FPS von der Grafikkarte begrenzt wird; DirectX-12- und Vulkan-Spiele steuern ihre Warteschlange selbst [1]. Für Spiele mit NVIDIA Reflex empfiehlt NVIDIA Reflex statt der Latenzeinstellung des Treibers [3].

## Nachteile & Risiken
In CPU-limitierten Spielen kann eine kürzere Warteschlange die Framerate leicht senken oder die Frametimes ungleichmäßiger machen. Die Stufe „Ultra“ wird hier nicht angeboten.

## Wann du es nicht nutzen solltest
In Spielen mit NVIDIA Reflex (schalte dort Reflex ein [3]) oder wenn die Frametimes ungleichmäßig werden.

## Quellen
1. https://www.nvidia.com/en-us/geforce/news/gamescom-2019-game-ready-driver/
2. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
3. https://www.nvidia.com/en-us/geforce/news/reflex-low-latency-platform/
4. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
