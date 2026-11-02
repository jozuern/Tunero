# Neustart ausstehend

## Zusammenfassung
::: variant update
Windows Update wartet auf einen Neustart, um Updates fertig zu installieren. Starte neu, bevor du Einstellungen änderst oder Leistung misst.
:::
::: variant servicing
Windows wartet auf einen Neustart, um ein Feature oder eine Komponente fertig hinzuzufügen oder zu entfernen. Starte neu, bevor du Einstellungen änderst oder misst.
:::
::: variant default
Prüft, ob Windows auf einen Neustart wartet, um Updates oder eine Feature-Änderung abzuschließen.
:::

## Warum das wichtig ist
Updates und Features müssen oft Dateien ersetzen, die gerade benutzt werden. Windows erledigt das dann beim nächsten Neustart [1]. Bis dahin ist das Update nicht fertig: Die alten Dateien sind noch in Gebrauch, und andere Installationen können sich weigern zu starten [1]. Messungen und Änderungen in diesem Zustand kann der Neustart wieder verändern. Vergleiche die Leistung deshalb erst danach.

## Wie wir es erkennen
Wir suchen die beiden Schlüssel, die Windows setzt, solange es wartet: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` für Windows Update und `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending` für das Hinzufügen oder Entfernen von Windows-Features [2][3]. Ausstehende Dateiumbenennungen zählen nicht: Programme tragen sie oft ein, und sie brauchen keinen baldigen Neustart.

## So behebst du es
::: variant update
1. Speichere deine Arbeit und starte über Start > Ein/Aus > **Aktualisieren und neu starten** neu.
2. Starte danach den Scan erneut. Manche Updates brauchen einen zweiten Neustart [1].
:::
::: variant servicing
1. Speichere deine Arbeit und starte den PC neu.
2. Starte danach den Scan erneut. Erscheint der Hinweis nach zwei oder drei Neustarts noch immer, wurde eine Installation nicht fertig: Installiere die neuesten Updates oder das Feature noch einmal [1].
:::
::: variant default
Nichts zu tun.
:::

## So prüfst du die Behebung
Starte den Scan nach dem Neustart erneut. Beide Zeilen unter „Was wir gefunden haben“ sollten „Nein“ zeigen.

## Quellen
1. https://learn.microsoft.com/en-us/exchange/plan-and-deploy/deployment-ref/ms-exch-setupreadiness-pendingrebootwindowscomponents
2. https://learn.microsoft.com/en-us/intune/configmgr/core/clients/manage/manage-clients
3. https://learn.microsoft.com/en-us/intune/configmgr/core/servers/deploy/install/list-of-prerequisite-checks
