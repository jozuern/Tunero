# Auf den Energiesparplan „Ausbalanciert“ wechseln

## Zusammenfassung
Wechselt auf den Windows-Standard „Ausbalanciert“: statt Energiesparmodus wieder voller CPU-Takt, in den Profilen „Leise und kühl“ und „Akku“ statt Höchstleistung weniger Wärme und Lärm.

## So funktioniert es
Windows-Energiesparpläne legen fest, wie der Prozessor seinen Takt hebt und senkt, wann er Kerne parkt und wie Geräte Strom sparen. Der Energiesparmodus senkt die Leistung, um Strom zu sparen; „Ausbalanciert“ passt Leistung und Verbrauch dem Bedarf an; „Höchstleistung“ und darauf aufbauende Pläne wie „Ultimative Leistung“ liefern maximale Leistung auf Kosten eines höheren Stromverbrauchs [1]. In den Windows-Standardwerten parkt der Energiesparmodus im Netzbetrieb außerdem CPU-Kerne, „Ausbalanciert“ nicht.

## Warum es helfen kann
Ein Spiel auf einer CPU, die der Energiesparmodus bremst, bekommt weniger Bilder und unruhigere Frametimes. „Ausbalanciert“ hebt die Grenze auf, ohne den Leerlaufverbrauch nennenswert zu erhöhen.

In den Profilen „Leise und kühl“ und „Akku“ ist es umgekehrt: Ein Höchstleistungsplan verbraucht bei wenig Last mehr Strom als nötig, also mehr Wärme, mehr Lüfterlärm und weniger Akkulaufzeit. „Ausbalanciert“ senkt den Verbrauch, wenn wenig zu tun ist, und erreicht unter Last trotzdem den vollen Takt [1].

## Belege
Vom Energiesparmodus aus hängt der Effekt vom Prozessor und von der Einrichtung des Plans ab, daher bewertet die App ihn als situationsabhängig. Von „Höchstleistung“ aus geht es um Verbrauch und Wärme, nicht um FPS: Im Netzbetrieb lässt „Ausbalanciert“ den Takt bei wenig Last weit sinken, „Höchstleistung“ hält ihn oben [2]. Deshalb bekommt der Wechsel nur eine Bewertung fürs Spielen, solange der PC den Energiesparmodus nutzt.

## Nachteile & Risiken
Unter Last höherer Verbrauch als im Energiesparmodus. Gegenüber „Höchstleistung“ taktet der Prozessor bei wenig Last herunter und muss bei neuer Last erst wieder hochtakten.

## Wann du es nicht nutzen solltest
Fürs Spielen ist es nicht nötig, wenn du bereits „Ausbalanciert“, „Höchstleistung“ oder einen eigenen Leistungsplan nutzt. Deshalb empfiehlt die App es statt „Höchstleistung“ nur in den Profilen „Leise und kühl“ und „Akku“.

## Quellen
1. https://learn.microsoft.com/en-us/windows/win32/power/power-policy-settings
2. https://learn.microsoft.com/en-us/windows-server/administration/performance-tuning/hardware/power/power-performance-tuning
