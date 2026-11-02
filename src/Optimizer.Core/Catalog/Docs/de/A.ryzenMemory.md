# Speichergeschwindigkeit bei Ryzen

## Zusammenfassung
::: variant aboveSync
Der Speicher läuft mit {{speed}} MT/s, über dem Bereich, in dem der Ryzen-Speichercontroller meist synchron läuft. Prüfe, ob er auf halben Takt gewechselt hat.
:::
::: variant slowKit
Der Speicher läuft mit {{speed}} MT/s, unter dem Bereich, in dem Ryzen meist am besten läuft ({{low}} bis {{high}} MT/s).
:::
::: variant default
Prüft, ob die Speichergeschwindigkeit zum Ryzen-Speichercontroller passt.
:::

## Warum das wichtig ist
Bei Ryzen laufen der Speichercontroller und die Verbindung zwischen den Chips des Prozessors (Infinity Fabric) mit Takten, die an den Speichertakt gekoppelt sind. Für Spiele zählen deshalb Speichergeschwindigkeit und Speicherlatenz.
::: if am4
Bei AM4 berichten Tester häufig, dass der Infinity-Fabric-Takt (FCLK) dem Speichertakt bis etwa DDR4-3600 im Verhältnis 1:1 folgen kann, bei vielen Zen-2- und Zen-3-Prozessoren bis etwa DDR4-3800. Darüber wechselt er meist auf 2:1, was Latenz kostet. Diese Grenzen sind keine AMD-Spezifikation und unterscheiden sich von Prozessor zu Prozessor.
:::
::: if am5
Bei AM5 berichten Tester häufig, dass der Takt des Speichercontrollers (UCLK) standardmäßig bis etwa DDR5-6000 im Verhältnis 1:1 mit dem Speichertakt läuft und das BIOS ihn darüber auf halben Takt (1:2) stellt, was Latenz kostet. Das ist keine AMD-Spezifikation und hängt von Prozessor und BIOS ab.
:::
AMDs offizielle Speicherspezifikation mit zwei Modulen ist DDR4-3200 bei Ryzen 5000 [1], DDR5-5200 bei Ryzen 7000 [2] und DDR5-5600 bei Ryzen 9000 [3]. Schnellere Takte laufen über ein EXPO- oder XMP-Profil, das AMD als Speicherübertaktung beschreibt [4]. Mit vier DDR5-Modulen gibt AMD DDR5-3600 an [2][3].

## Wie wir es erkennen
Wir lesen die eingestellte Speichergeschwindigkeit und die Nenngeschwindigkeit aus den Teilenummern der Module. Das untere Ende des Bereichs ist AMDs offizielle Geschwindigkeit für zwei Module bei Ryzen 5000 (DDR4-3200) [1] und Ryzen 7000 (DDR5-5200) [2]; bei anderen Ryzen-Prozessoren ist es ein Richtwert (Ryzen 9000 ist zum Beispiel für DDR5-5600 angegeben [3]). Das obere Ende ist eine typische 1:1-Grenze aus Tests. Liegt die Geschwindigkeit unter dem Bereich und ist die Nenngeschwindigkeit des Kits unbekannt, lautet das Ergebnis „Unbekannt“, weil ein langsames Kit und ein ausgeschaltetes Speicherprofil gleich aussehen. Bei vier DDR5-Modulen gibt es keinen Rat zum Aufrüsten, weil AMD für vier Module eine niedrigere Geschwindigkeit angibt. Das tatsächliche Verhältnis von FCLK oder UCLK kann Windows nicht lesen. Das hier ist also eine Empfehlung, keine Messung.

## So behebst du es
::: variant aboveSync
1. **BitLocker:** Prüfe, bevor du BIOS-Einstellungen änderst, ob BitLocker oder die Geräteverschlüsselung an ist. Wenn ja, setze den Schutz vorher aus (Start > **BitLocker verwalten** > **Schutz anhalten**) oder halte den Wiederherstellungsschlüssel bereit. Oft ist er in deinem Microsoft-Konto gespeichert. Nach einem BIOS-Update oder einer Änderung am TPM oder an der Startkonfiguration kann Windows beim nächsten Start danach fragen [5][6].
::: if am4
2. Prüfe im BIOS, ob **FCLK** auf der Hälfte der Speichergeschwindigkeit steht (bei DDR4-4000: 2000 MHz). Läuft das System so nicht stabil, kann eine niedrigere Speichergeschwindigkeit mit 1:1 die bessere Wahl sein.
:::
::: if am5
2. Suche im BIOS nach **UCLK DIV1 MODE** (Bezeichnung je nach Hersteller) und stelle **UCLK = MEMCLK** ein. Läuft das System so nicht stabil, kann eine niedrigere Speichergeschwindigkeit mit 1:1 die bessere Wahl sein.
:::
:::
::: variant slowKit
1. Für ein Upgrade passt zu dieser Plattform ein Kit im Bereich {{low}} bis {{high}} MT/s mit niedrigen CL-Timings. Takte über AMDs Spezifikation brauchen das EXPO- oder XMP-Profil des Kits [4]. Nutze zwei Module in den Steckplätzen, die das Handbuch empfiehlt.
:::
::: variant default
Nichts zu tun.
:::

## So prüfst du die Behebung
Starte den Scan erneut. Für das Taktverhältnis zeigen Tools wie ZenTimings oder das BIOS FCLK, UCLK und MCLK.

## Quellen
1. https://www.amd.com/de/products/processors/desktops/ryzen/5000-series/amd-ryzen-7-5800x3d.html
2. https://www.amd.com/de/products/processors/desktops/ryzen/7000-series/amd-ryzen-7-7800x3d.html
3. https://www.amd.com/de/products/processors/desktops/ryzen/9000-series/amd-ryzen-7-9800x3d.html
4. https://www.amd.com/de/products/processors/technologies/expo.html
5. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
6. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/operations-guide
