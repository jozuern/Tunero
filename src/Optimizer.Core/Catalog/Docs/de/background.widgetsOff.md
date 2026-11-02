# Widgets aus

## Zusammenfassung
Schaltet das Widgets-Board und seinen Newsfeed per Richtlinie für den PC ab, auch den Eintrag in der Taskleiste.

## So funktioniert es
Die Richtlinie AllowNewsAndInterests = 0 („Widgets zulassen“) deaktiviert die gesamte Widgets-Funktion, auch die Inhalte in der Taskleiste [1]. Aktuelle Versionen von Windows 11 lassen diesen Wert nur Windows selbst schreiben, solange der User Choice Protection Driver (UCPD) läuft, auch Administratoren nicht: Ein VM-Test unter 26H2 Home wurde mit laufendem Treiber abgewiesen und gelang mit deaktiviertem Dienst. Tunero bietet den Tweak deshalb nur auf PCs an, auf denen dieser Treiber aus ist. Sonst schaltest du die Widgets unter **Einstellungen > Personalisierung > Taskleiste** ab.

## Warum es helfen kann
Das Widgets-Board lädt Webinhalte; mit der Richtlinie werden sie nicht mehr geladen.

## Belege
Keine veröffentlichte Messung zeigt einen Effekt auf FPS oder RAM. Der Nutzen ist vor allem weniger Ablenkung.

## Nachteile & Risiken
Kein Wetter und keine Nachrichten mehr in der Taskleiste.

## Wann du es nicht nutzen solltest
Behalte die Widgets, wenn du sie nutzt.

## Quellen
1. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-newsandinterests
