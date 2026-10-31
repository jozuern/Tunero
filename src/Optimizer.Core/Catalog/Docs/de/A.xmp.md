# RAM unter Nenngeschwindigkeit (XMP / EXPO)

## Zusammenfassung
::: status Problem
Dein RAM ist für {{rated}} MT/s ausgelegt, läuft aber mit {{configured}} MT/s. Mit {{profileName}} im BIOS läuft er meist mit seiner Nenngeschwindigkeit.
:::
::: status Ok
Dein RAM läuft mit seiner Nenngeschwindigkeit von {{rated}} MT/s. {{profileName}} (oder eine gleichwertige Einstellung) ist aktiv.
:::
::: status Info
::: variant fourDimms
Vier DDR5-Module laufen mit {{configured}} MT/s, unter den {{rated}} MT/s des Kits. AMD gibt für vier Module bei Ryzen DDR5-3600 an, das ist also zu erwarten.
:::
::: variant fourDimmsIntel
Vier DDR5-Module laufen mit {{configured}} MT/s, unter den {{rated}} MT/s des Kits. Intel gibt für vier Module bei diesem Prozessor {{fourDimmMts}} MT/s oder weniger an, das ist also zu erwarten.
:::
::: variant laptop
Dein RAM ist für {{rated}} MT/s ausgelegt, läuft aber mit {{configured}} MT/s. Die meisten Laptops legen den Speichertakt fest, eine Einstellung dafür fehlt dann.
:::
::: variant intelLocked
Dein RAM läuft mit {{configured}} MT/s, dem Wert, den Intel für deinen Prozessor angibt. Mainboards dieser Generation mit B- oder H-Chipsatz können ihn mit XMP nicht erhöhen.
:::
::: variant default
Prüft, ob der RAM mit der Geschwindigkeit aus seiner Teilenummer läuft. Dafür ist meist XMP oder EXPO im BIOS nötig.
:::
:::
::: status Unknown,Unsupported
Prüft, ob der RAM mit der Geschwindigkeit aus seiner Teilenummer läuft. Dafür ist meist XMP oder EXPO im BIOS nötig.
:::

## Warum das wichtig ist
Speichermodule starten zuerst mit einer sicheren Standardgeschwindigkeit (JEDEC) [1]. Die höhere Geschwindigkeit von der Verpackung ist als Profil gespeichert (Intel XMP, AMD EXPO) und muss im BIOS eingeschaltet werden [1][2]. ASUS-Mainboards zeigen **DOCP**, wenn das Kit das Profil des jeweils anderen Herstellers trägt, zum Beispiel ein XMP-Kit auf einem AMD-Mainboard [3]. Intel und AMD bezeichnen diese Profile als Speicherübertaktung, und laut Intel kann eine Änderung von Takt oder Spannung die Garantie gefährden und die Stabilität verringern [1][2]. Ohne Profil läuft der Speicher langsamer als angegeben, und Spiele, die von Speicherbandbreite und -latenz abhängen, verlieren FPS. TechSpot hat 13 Spiele mit DDR5 in Grundgeschwindigkeit und mit Profil gemessen: im Schnitt 12 % mehr mit einem Ryzen 7 7700X und 11 % mit einem Core i7-12700K, in Hogwarts Legacy bis zu 17 % und 20 % mit 29 % und 31 % besseren 1-%-Lows, aber nur 4 % mit einem Ryzen 7 7800X3D, dessen großer Cache die Speichergeschwindigkeit verdeckt, und mit einem Core i7-14700K [18].
::: variant laptop
Die meisten Laptops bieten kein XMP an. Ihre Speichergeschwindigkeit legt der Hersteller fest.
:::
::: variant fourDimms
Mit vier DDR5-Modulen gibt AMD für Ryzen 7000 und 9000 DDR5-3600 an [4][5]. Eine niedrigere Geschwindigkeit als auf dem Kit ist also zu erwarten und keine vergessene Einstellung.
:::
::: variant fourDimmsIntel
Mit zwei DDR5-Modulen pro Kanal (vier Modulen) geben Intels Datenblätter bei Core-Prozessoren der 13. und 14. Generation 4000 MT/s für Single-Rank- und 3600 MT/s für Dual-Rank-Module an [16], bei Core Ultra 200S 4800 und 4400 MT/s [17]. Eine niedrigere Geschwindigkeit als auf dem Kit ist also zu erwarten; XMP würde die Module über Intels Spezifikation betreiben.
:::
::: variant intelLocked
Für Intel-Core-Prozessoren der 8. bis 10. Generation führt Intel Chipsätze wie B360, H370, B460 und H470 ohne Unterstützung für Speicherübertaktung, den Z490 dagegen mit, ebenso den Z590, der auch Prozessoren der 10. Generation aufnimmt [9][10][11][12][13][19][20]. Auf so einem Mainboard läuft der Speicher mit der Geschwindigkeit, die für den Prozessor angegeben ist, zum Beispiel DDR4-2666 bei einem Core i5-9400F und DDR4-2933 bei einem Core i7-10700 [14][15], egal was das XMP-Profil des Kits sagt. Dein Mainboard ({{board}}) wurde nicht als Z370-, Z390-, Z490- oder Z590-Modell erkannt.
:::

## Wie wir es erkennen
Wir lesen jedes Speichermodul aus Windows (WMI): die Teilenummer und die Geschwindigkeit, mit der es gerade läuft (`ConfiguredClockSpeed`). Die Nenngeschwindigkeit ermitteln wir aus der Teilenummer, mit den Katalogtabellen für Corsair, G.Skill, Kingston, Crucial, TeamGroup und Patriot. `Win32_PhysicalMemory.Speed` nutzen wir dafür nicht, weil es meist nur die Standardgeschwindigkeit meldet. Unbekannte Teilenummern ergeben „Unbekannt“ und keinen Rat. Laufen vier DDR5-Module an einem Ryzen unter der Kit-Angabe, gibt es wegen AMDs Spezifikation eine Information statt eines Problems; ebenso an einem Intel Core der 13. oder 14. Generation oder einem Core Ultra 200S, für die Intel mit vier Modulen eine niedrigere Geschwindigkeit angibt. Ebenso bei Speicher mit 2400, 2666 oder 2933 MT/s an einem Intel-Prozessor der 8. bis 10. Generation, wenn der Name des Mainboards keinen Z370-, Z390-, Z490- oder Z590-Chipsatz zeigt (Fertig-PCs melden oft nur eine Teilenummer).

## So behebst du es
::: variant fourDimms,fourDimmsIntel
Nichts zu tun. Für die volle Geschwindigkeit des Kits bräuchtest du zwei statt vier Module.
:::
::: variant intelLocked
Auf einem Mainboard mit B- oder H-Chipsatz ist nichts zu tun. Hat dein Mainboard doch einen Z370-, Z390-, Z490- oder Z590-Chipsatz, suche im BIOS auf der Übertaktungs- oder „Tweaker“-Seite nach **XMP**, wähle das erste Profil und speichere. Ein schnelleres Kit lohnt sich nur zusammen mit einem Z-Mainboard.
:::
::: variant default,laptop
1. **BitLocker:** Prüfe, bevor du BIOS-Einstellungen änderst, ob BitLocker oder die Geräteverschlüsselung an ist. Wenn ja, setze den Schutz vorher aus (Start > **BitLocker verwalten** > **Schutz anhalten**) oder halte den Wiederherstellungsschlüssel bereit. Oft ist er in deinem Microsoft-Konto gespeichert. Nach einem BIOS-Update oder einer Änderung am TPM oder an der Startkonfiguration kann Windows beim nächsten Start danach fragen [7][8].
2. Starte neu ins BIOS: **Einstellungen > System > Wiederherstellung > Erweiterter Start > Jetzt neu starten**, dann **Problembehandlung > Erweiterte Optionen > UEFI-Firmwareeinstellungen** (je nach Version „UEFI-Einstellungen“) [6]. Bei vielen PCs öffnet es sich auch, wenn du beim Start **Entf** oder **F2** drückst.
::: if menuPath
3. Auf deinem {{board}}: **{{menuPath}}**.
::: if menuUnverified
   Dieser Pfad ist noch nicht mit dem Handbuch deines Mainboards abgeglichen. Menünamen unterscheiden sich je nach Board und BIOS-Version. Passt der Pfad nicht, suche die Einstellung über ihren Namen.
:::
:::
::: ifnot menuPath
3. Suche die Einstellung für das Speicherprofil: **XMP**, **EXPO**, **DOCP** oder **A-XMP**, meist auf der Übertaktungs- oder „Tweaker“-Seite.
:::
4. Wähle das erste Profil, speichere und beende (meist **F10**).
5. Der erste Start kann länger dauern, weil das Mainboard den Speicher einmisst. Startet der PC nicht oder stürzt er ab, aktualisiere zuerst das BIOS oder probiere das zweite Profil. Bleibt er instabil, schalte das Profil wieder aus: Ein stabiler PC mit Standardtakt ist besser als ein instabiler schneller.
:::

## So prüfst du die Behebung
Starte den Scan erneut. Die aktuelle Geschwindigkeit sollte der Nenngeschwindigkeit entsprechen. Im Task-Manager unter Leistung > Arbeitsspeicher > **Geschwindigkeit** steht derselbe Wert.

## Quellen
1. https://www.intel.com/content/www/us/en/gaming/extreme-memory-profile-xmp.html
2. https://www.amd.com/de/products/processors/technologies/expo.html
3. https://www.asus.com/de/support/faq/1042256/
4. https://www.amd.com/de/products/processors/desktops/ryzen/7000-series/amd-ryzen-7-7800x3d.html
5. https://www.amd.com/de/products/processors/desktops/ryzen/9000-series/amd-ryzen-7-9800x3d.html
6. https://support.microsoft.com/de-de/windows/experience/enable-virtualization-on-windows
7. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
8. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/operations-guide
9. https://www.intel.com/content/www/us/en/products/sku/133332/intel-b360-chipset/specifications.html
10. https://www.intel.com/content/www/us/en/products/sku/133284/intel-h370-chipset/specifications.html
11. https://www.intel.com/content/www/us/en/products/sku/201841/intel-b460-chipset/specifications.html
12. https://www.intel.com/content/www/us/en/products/sku/201835/intel-h470-chipset/specifications.html
13. https://www.intel.com/content/www/us/en/products/sku/201834/intel-z490-chipset/specifications.html
14. https://www.intel.com/content/www/us/en/products/sku/190883/intel-core-i59400f-processor-9m-cache-up-to-4-10-ghz/specifications.html
15. https://www.intel.com/content/www/us/en/products/sku/199316/intel-core-i710700-processor-16m-cache-up-to-4-80-ghz/specifications.html
16. https://edc.intel.com/content/www/us/en/design/products/platforms/details/raptor-lake-s/13th-generation-core-processors-datasheet-volume-1-of-2/005/processor-sku-support-matrix/
17. https://edc.intel.com/content/www/us/en/design/products/platforms/details/arrow-lake-s/core-ultra-200s-series-processors-datasheet-volume-1-of-2/processor-sku-support-matrix/
18. https://www.techspot.com/review/2866-ddr5-ram-stock-vs-xmp-expo/
19. https://www.intel.com/content/www/us/en/products/sku/196612/intel-z590-chipset/specifications.html
20. https://www.intel.com/content/www/us/en/products/sku/196612/intel-z590-chipset/compatible.html
