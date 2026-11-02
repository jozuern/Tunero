# NVIDIA-Shader-Cache: unbegrenzt

## Zusammenfassung
Lässt den NVIDIA-Shader-Cache auf dem Laufwerk ohne Größenlimit des Treibers wachsen, damit kompilierte Shader nicht gelöscht und neu kompiliert werden.

## So funktioniert es
Spiele kompilieren ihre Shader für deine Grafikkarte; der Treiber speichert das Ergebnis auf dem Datenträger und nutzt es beim nächsten Mal wieder. „Shader-Cache-Größe“ in der NVIDIA Systemsteuerung legt fest, wie viel Speicherplatz der Treiber dafür höchstens nutzen darf: deaktiviert, unbegrenzt oder eine Größe von 128 MB bis 100 GB [1]. Die App schreibt den Höchstwert, den NVIDIAs Header-Datei für diese Einstellung festlegt, ins globale Profil [3].

## Warum es helfen kann
Spielst du viele große Spiele, werden bei vollem Cache Shader neu kompiliert. Das zeigt sich als Ruckeln in den ersten Minuten einer Sitzung.

## Belege
Der Treiberstandard liegt bei 16 GB [2]. NVIDIA rät zu einem Limit, das für die Spiele reicht, die du meist spielst [1]; unbegrenzt bringt nur etwas, wenn deine Spiele mehr als den Standard brauchen.

## Nachteile & Risiken
Mit vielen Spielen kann der Cache viel Speicherplatz belegen. Ein neuer Treiber löscht ihn, und die ersten Sitzungen danach können ruckeln, während Shader neu kompiliert werden [1]. Du kannst ihn in den NVIDIA-Cache-Ordnern von Hand löschen [2].

## Wann du es nicht nutzen solltest
Auf einem kleinen, fast vollen Systemlaufwerk.

## Quellen
1. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
2. https://nvidia.custhelp.com/app/answers/detail/a_id/5735/~/deleting-nvidia-shader-cache-files
3. https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h
