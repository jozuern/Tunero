# Auto HDR an

## Zusammenfassung
Ältere DirectX-11- und -12-Spiele, die nur SDR ausgeben, werden auf einem HDR-Bildschirm mit HDR dargestellt. HDR selbst muss an sein.

## So funktioniert es
Auto HDR erweitert Farbraum und Helligkeit von SDR-Spielen mit DirectX 11 oder 12 auf einem HDR-fähigen Bildschirm; es liegt unter Einstellungen > System > Anzeige > HDR [1]. Die App setzt das Token AutoHDREnable in DirectXUserGlobalSettings, neben den Optimierungen für Spiele im Fenstermodus. Microsoft beschreibt den Schalter [1], der Registry-Wert dahinter ist aber nicht dokumentiert; die App schreibt denselben Wert, den Windows beim Umlegen des Schalters speichert. Solange kein Test auf einer echten Windows-Installation die Wirkung bestätigt, bleibt die Option eine Vorschau.

## Warum es helfen kann
Ältere Spiele sehen auf einem HDR-Monitor eher wie HDR-Spiele aus.

## Belege
Eine Frage der Optik. Laut Microsoft schaltet Auto HDR auch die Optimierungen für Spiele im Fenstermodus ein [2]; DirectX-10- und -11-Spiele im Fenster geben ihre Bilder dann anders aus. Auto HDR selbst braucht etwas Rechenleistung der GPU, was Spiele laut Microsoft nicht wesentlich beeinträchtigen soll [3]; eine Messung der Framerate veröffentlicht Microsoft nicht.

## Nachteile & Risiken
Das Ergebnis hängt vom Spiel ab; manche wirken blass oder zu hell. Ohne HDR-Bildschirm oder mit HDR aus ändert sich nichts.

## Wann du es nicht nutzen solltest
Wenn du keinen HDR-Bildschirm hast oder Spiele im Originallook magst.

## Quellen
1. https://support.microsoft.com/en-us/windows/hardware/display-graphics/use-auto-hdr-for-better-gaming-in-windows
2. https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952
3. https://devblogs.microsoft.com/directx/auto-hdr-preview-for-pc-available-today/
