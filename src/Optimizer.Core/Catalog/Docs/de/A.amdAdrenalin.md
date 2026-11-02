# AMD-Software-Einstellungen fürs Spielen

## Zusammenfassung
::: status Info
Einstellungen in AMD Software: Adrenalin Edition, die fürs Spielen auf der {{gpu}} zählen. Die App erklärt sie; geändert werden sie in AMD Software.
:::
::: status Ok,Unknown,Problem,Unsupported
Empfohlene Einstellungen in AMD Software fürs Spielen.
:::

## Warum das wichtig ist
Manche Radeon-Funktionen opfern Bildqualität oder FPS für weniger Verbrauch oder ein flüssigeres Bild, und ihre Standardwerte hängen von der Treiberversion und dem bei der Installation gewählten Profil ab. AMDs Einstellungsbibliothek (ADLX) kann diese Werte ändern [1]. Sie ist aber eine native Bibliothek, die diese App nicht mitbringt. Deshalb erklärt die App die Einstellungen, statt sie zu ändern.

## Wie wir es erkennen
Wir haben eine Radeon-Grafikkarte mit AMD-Treiber gefunden. Workstation-Karten (Radeon Pro, FirePro, Instinct) lassen wir aus, weil diese Schritte für AMD Software: Adrenalin Edition gedacht sind.

## So behebst du es
1. Öffne **AMD Software: Adrenalin Edition > Gaming > Grafik** (global oder pro Spiel). Die Menünamen können sich je nach Treiberversion unterscheiden.
2. **Radeon Anti-Lag:** Ein. Laut AMD senkt es den Input-Lag in GPU-limitierten Fällen, indem es die Arbeit der CPU zeitlich auf die GPU abstimmt [1].
3. **Radeon Chill:** Aus, außer du willst die Framerate für weniger Wärme und Lautstärke begrenzen. Anti-Lag und Chill können nicht gleichzeitig an sein [1].
4. **Radeon Boost:** Aus, wenn dir die niedrigere Auflösung bei schnellen Bewegungen auffällt; es erhöht die Framerate, indem es während der Bewegung mit geringerer Auflösung rendert.
5. **Auf vertikale Aktualisierung warten:** „Aus, außer von der Anwendung festgelegt“, und **Enhanced Sync** aus, wenn du mit FreeSync Ruckler siehst.
6. **Gaming > Anzeige:** Schalte **AMD FreeSync** für einen FreeSync-Monitor ein.
7. **Aufnahme und Streaming:** Schalte die Sofortwiederholung ab, wenn du sie nicht nutzt; sie nimmt im Hintergrund auf.

## So prüfst du die Behebung
AMD Software zeigt die aktiven Werte pro Spiel. Der Benchmark auf der Seite Zustand misst die Wirkung.

## Quellen
1. https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/3d-graphics/iadlx3dantilag/setenabled/
