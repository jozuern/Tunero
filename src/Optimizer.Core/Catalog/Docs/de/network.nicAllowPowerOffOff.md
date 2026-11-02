# Windows darf den Netzwerkadapter nicht abschalten

## Zusammenfassung
Entfernt bei kabelgebundenen Adaptern den Haken bei „Computer kann das Gerät ausschalten, um Energie zu sparen“. Wirkt nur beim Energiesparmodus und verhindert das Aufwecken über das Netzwerk.

## So funktioniert es
Die Registerkarte Energieverwaltung des Adapters im Geräte-Manager wird als Wert PnPCapabilities gespeichert. Laut Microsoft steuert die Option nur, wie der Adapter behandelt wird, wenn der PC in den Energiesparmodus geht: Ist sie an, versetzt Windows den Adapter in einen Stromsparzustand und wieder zurück. Ist sie aus, hält Windows den Adapter an und startet ihn beim Aufwachen neu. Wegen Untätigkeit schaltet Windows den Adapter nie ab [1]. Der Wert 24 entfernt die Option und verhindert außerdem, dass der Adapter den PC aufweckt [1]. Die App schreibt ihn für physische Ethernet-Adapter und startet sie neu. Rückgängig machen stellt den vorherigen Zustand wieder her.

## Warum es helfen kann
Manche Treiber geben an, Energiesparzustände zu unterstützen, kommen nach dem Aufwachen des PCs aber nicht richtig zurück. Ein Neustart des Adapters beim Aufwachen umgeht das [1].

## Belege
Microsoft hat diesen Wert für Treiber dokumentiert, die ihr Verhalten im Energiesparmodus falsch melden, in einem Supportartikel, den Microsoft inzwischen zurückgezogen hat. Der Quellenlink zeigt die letzte veröffentlichte Fassung im Dokumentations-Repository von Microsoft [1]. Der Artikel wurde für Windows 7 geschrieben und gilt nicht für neuere NetAdapterCx-Treiber [1]. Während du spielst, ändert sich nichts.

## Nachteile & Risiken
Wake-on-LAN und das Aufwecken des PCs über das Netzwerk funktionieren nicht mehr.

## Wann du es nicht nutzen solltest
Wenn deine Verbindung nach dem Energiesparmodus funktioniert oder du den PC über das Netzwerk aufweckst.

## Quellen
1. https://github.com/MicrosoftDocs/SupportArticles-docs/blob/63017aecf99ce46fc89302444a1b2f4537698ae4/support/windows-client/networking/power-management-on-network-adapter.md
