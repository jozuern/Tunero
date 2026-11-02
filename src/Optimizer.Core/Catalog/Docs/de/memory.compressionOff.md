# Speicherkomprimierung aus

## Zusammenfassung
Verhindert, dass Windows ungenutzte Speicherseiten im RAM komprimiert. Nur ab 16 GB angeboten. Wirkung umstritten: Keine veröffentlichte Messung zeigt einen Vorteil in Spielen.

## So funktioniert es
Wird der Speicher knapp, komprimiert Windows ungenutzte Seiten und behält sie im RAM, statt sie auf das Laufwerk zu schreiben [2]. Die komprimierten Seiten liegen im Prozess „System“, der deshalb im Task-Manager groß wirken kann [2]. Die App schaltet das mit Disable-MMAgent -MemoryCompression ab [1]. Es wirkt nach einem Neustart.

## Warum es helfen kann
Komprimieren und Entpacken kosten etwas CPU-Zeit. Mit viel freiem RAM wird wenig komprimiert, die mögliche Ersparnis ist also klein.

## Belege
Microsoft hat die Komprimierung eingeführt, um mehr Daten im RAM zu halten und die Reaktionszeit zu verbessern [2]. Wir haben keine Messung von Microsoft oder einem Hardwarehersteller gefunden, die mit abgeschalteter Komprimierung mehr FPS oder weniger Ruckler zeigt. Die Grenze von 16 GB ist unsere eigene vorsichtige Wahl, kein veröffentlichter Richtwert.

## Nachteile & Risiken
Wird der Speicher knapp, landen Seiten, die sonst komprimiert würden, in der Auslagerungsdatei. Sie von dort zurückzulesen dauert länger als das Entpacken.

## Wann du es nicht nutzen solltest
Nicht nutzen, wenn Spiele oder andere Programme oft den Großteil deines RAMs belegen.

## Quellen
1. https://learn.microsoft.com/en-us/powershell/module/mmagent/disable-mmagent
2. https://blogs.windows.com/windows-insider/2015/08/18/announcing-windows-10-insider-preview-build-10525/
