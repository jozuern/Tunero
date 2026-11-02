# NVIDIA: maximale Leistung für dieses Spiel bevorzugen

## Zusammenfassung
Setzt „Maximale Leistung bevorzugen“ nur im NVIDIA-Profil dieses Spiels: hoher Takt, solange es läuft, alles andere bleibt, wie es ist. Eine Wirkung auf die Frametimes ist noch nicht gemessen.

## So funktioniert es
NVIDIA speichert Einstellungen pro Spiel in Treiberprofilen [1]. Die App schreibt den Energieverwaltungsmodus in das Profil, zu dem dieses Spiel gehört. „Maximale Leistung bevorzugen“ betreibt die GPU bei den meisten 3D-Anwendungen mit maximaler Leistung [2]. Hat der Treiber kein Profil für das Spiel, legt die App eines mit dem Namen „Tunero: “ und dem Dateinamen des Spiels an. Rückgängig machen stellt den vorherigen Wert des Profils wieder her oder entfernt die Einstellung, wenn es keinen hatte.

## Warum es helfen kann
Im adaptiven Modus senkt der Treiber die Leistungsstufe der GPU, wenn eine 3D-Anwendung weniger braucht [2]. In leichten oder CPU-limitierten Spielen kann die Grafikkarte so zwischen den Frames heruntertakten. Maximale Leistung soll ungleichmäßige Frametimes vermeiden.

## Belege
Die Wirkung hängt von der Situation ab; in anspruchsvollen Spielen läuft die Karte ohnehin mit vollem Takt. Eine Messung, die eine Wirkung auf die Frametimes zeigt, gibt es noch nicht.

## Nachteile & Risiken
Mehr Stromverbrauch und Wärme, solange das Spiel läuft. Hat die App für das Spiel ein Profil angelegt, löscht Rückgängig machen es wieder, sofern es keine weiteren Einstellungen enthält.

## Wann du es nicht nutzen solltest
Auf Laptops im Akkubetrieb oder in Spielen, die die GPU ohnehin voll auslasten.

## Quellen
1. https://archive.docs.nvidia.com/gameworks/content/gameworkslibrary/coresdk/nvapi/group__drsapi.html
2. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm
