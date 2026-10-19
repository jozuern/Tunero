# Intel Application Optimization (APO)

## Zusammenfassung
::: status Info
Der {{cpu}} unterstützt Intel APO, das ändert, wie unterstützte Spiele die Kerne nutzen. Dafür braucht es den Dynamic-Tuning-Treiber; die APO-App ist optional.
:::
::: status Ok,Unknown,Problem,Unsupported
Prüft, ob der Prozessor Intel Application Optimization unterstützt.
:::

## Warum das wichtig ist
Intel APO steuert, wie die Threads eines unterstützten Spiels auf Performance- und Effizienzkerne verteilt werden. Intel veröffentlicht eine Liste unterstützter Spiele und schreibt, dass APO deren Leistung verbessern kann; es wirkt nur bei den Titeln auf dieser Liste [1]. Intel nennt geprüfte Prozessoren, zum Beispiel Core i5-14600K, i7-14700K, i9-14900K, die K-Modelle der Core Ultra 200S und einige mobile HX- und H-Modelle. Andere Prozessoren ab der 12. Generation werden nur eingeschränkt unterstützt [1]. APO läuft als Teil des Treibers für Intel Dynamic Tuning Technology (DTT), den der PC- oder Mainboard-Hersteller bereitstellt [1].
::: variant dttFound
Wir haben Dienste gefunden, die nach dem Treiber von Intel Dynamic Tuning Technology aussehen. Ihre Namen stammen aus unserer eigenen Liste, die auf echter Hardware noch nicht bestätigt ist: Der Treiber ist also wahrscheinlich installiert, aber nicht sicher.
:::
::: variant dttNotFound
Wir haben die Dienste von Intel Dynamic Tuning Technology, nach denen wir suchen, nicht gefunden. Das beweist nicht, dass der Treiber fehlt: Er nutzt vielleicht einen Namen, den wir nicht kennen.
:::

## Wie wir es erkennen
Wir gleichen den CPU-Namen mit der Liste geprüfter Modelle ab und suchen nach den Diensten von Dynamic Tuning. Ob APO aktiv ist, lässt sich nicht zuverlässig lesen.

## So behebst du es
::: ifnot laptop
1. Installiere den Treiber **Intel Dynamic Tuning Technology** von der Supportseite deines Mainboards ({{board}}). Er ist mainboardspezifisch und kommt deshalb nicht direkt von Intel [1].
:::
::: if laptop
1. Installiere den Treiber **Intel Dynamic Tuning Technology** von der Supportseite oder aus der App des Laptop-Herstellers. Er ist gerätespezifisch und kommt deshalb nicht direkt von Intel [1].
:::
2. **BitLocker:** Ist BitLocker oder die Geräteverschlüsselung an, setze den Schutz vorher aus (Start > **BitLocker verwalten** > **Schutz anhalten**) oder halte den Wiederherstellungsschlüssel bereit. Oft ist er in deinem Microsoft-Konto gespeichert. Nach einem BIOS-Update oder einer Änderung am TPM oder an der Startkonfiguration kann Windows beim nächsten Start danach fragen [2][3].
3. Halte das BIOS aktuell und prüfe, ob darin Intel Dynamic Tuning Technology oder Intel Platform Innovation Framework (IPF) eingeschaltet ist. Bei den meisten Systemen ist IPF ab Werk an; der Menüname unterscheidet sich je nach Mainboard [1].
4. Optional: Lade die App **Intel Application Optimization** aus dem Intel Download Center. Sie zeigt die unterstützten Spiele und schaltet APO pro Spiel ein oder aus [1].

## So prüfst du die Behebung
Die APO-App listet die unterstützten Spiele und zeigt, ob APO aktiv ist.

## Quellen
1. https://www.intel.com/content/www/us/en/support/articles/000095419/processors.html
2. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
3. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/operations-guide
