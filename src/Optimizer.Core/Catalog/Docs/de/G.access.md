# Spielzugang: Anforderungen der Anti-Cheats

## Zusammenfassung
::: status Problem
Ein installierter Anti-Cheat ({{antiCheats}}) braucht eine Sicherheitsfunktion, die auf diesem PC aus ist. Seine Spiele starten erst, wenn sie an ist.
:::
::: status Info
::: variant sometimes
Alle Pflichtfunktionen sind an. Mit {{antiCheats}} ist je nach Spiel, Modus oder Konto mehr nötig, etwa UEFI-Start, Secure Boot, TPM 2.0, VBS, Speicherintegrität oder IOMMU. Die Details zeigen jede einzeln.
:::
::: variant baseline
UEFI-Start, Secure Boot oder TPM 2.0 fehlt. Spiele mit strengen Anti-Cheats (Valorant, FACEIT, Battlefield 6, Call of Duty) starten nicht.
:::
:::
::: status Ok
::: variant none
Kein Kernel-Anti-Cheat gefunden. UEFI-Start, Secure Boot und TPM 2.0 sind an, das Minimum, das Spiele mit strengen Anti-Cheats verlangen.
:::
::: variant default
Alle Sicherheitsfunktionen, die die installierten Anti-Cheats verlangen, sind an.
:::
:::
::: status Unknown,Unsupported
Zeigt die Sicherheitsfunktionen, die Kernel-Anti-Cheats vor dem Spielstart prüfen. Es geht um den Zugang zu Spielen, nicht um FPS.
:::

## Warum das wichtig ist
Mehrere aktuelle Anti-Cheats erlauben das Spielen nur auf PCs mit einer geprüften Startkette. Fehlt eine geforderte Funktion, zeigt das Spiel einen Fehler und startet nicht. Deshalb empfiehlt diese App nie, diese Funktionen für mehr Leistung abzuschalten, und fragt vor einer Änderung, die sie abschaltet, nach, solange ein strenger Anti-Cheat installiert ist. Auch Call of Duty (Black Ops 7, Warzone, Modern Warfare 4) verlangt TPM 2.0 und Secure Boot mit UEFI-Start [11].
::: if vanguard
**Riot Vanguard** (Riot-Spiele) verlangt TPM 2.0 und Secure Boot [2][3]. Je nach PC kann es vor dem Spielstart zusätzlich Speicherintegrität (HVCI) und IOMMU verlangen [1]. Meldet Vanguard VAN: RESTRICTION, braucht es das Firmware-TPM (Intel PTT oder AMD fTPM); ein separates TPM-Modul reicht dann nicht [3].
:::
::: if faceit
**FACEIT** verlangt seit dem 25. November 2025 von allen Spielern UEFI-Start, TPM 2.0 und Secure Boot [4]. IOMMU zusammen mit virtualisierungsbasierter Sicherheit (VBS) wird in Wellen durchgesetzt, schon für viele Spieler und für alle über 3.000 Elo [4][5]. Manche Spieler sollen außerdem die Speicherintegrität einschalten [6].
:::
::: if javelin
**EA Javelin** kann in einigen EA-Spielen Secure Boot verlangen. EA hat einstellbare Secure-Boot-Anforderungen mit Battlefield 2042 eingeführt, und Battlefield 6 verlangt Secure Boot und TPM 2.0 [7][8].
::: if unverified_javelin
Der Dienstname, an dem wir Javelin erkennen, ist noch nicht bestätigt. Deshalb ist dieser Eintrag als nicht geprüft markiert.
:::
:::
::: if eac
**Easy Anti-Cheat** hat keine festen Anforderungen. Der Entwickler jedes Spiels kann Secure Boot, TPM oder IOMMU für einzelne Modi, einzelne Konten oder alle Spieler verlangen [9]. Unter Windows-Insider- und Beta-Builds verlangen Spiele, die es über Epic Online Services nutzen, Secure Boot, TPM und Speicherintegrität [10].
:::

## Wie wir es erkennen
Wir lesen den Firmware-Typ (UEFI oder Legacy), den Secure-Boot-Status, die TPM-Version, den Status der virtualisierungsbasierten Sicherheit und der Speicherintegrität (`Win32_DeviceGuard`) [14], die IOMMU-Unterstützung (den DMA-Schutz, den Windows für die virtualisierungsbasierte Sicherheit meldet) [14] und den Partitionsstil des Systemdatenträgers. Installierte Anti-Cheats erkennen wir an ihrer Dienst- und Treiberregistrierung, daher finden wir Vanguard auch im Modus „bei Bedarf“. IOMMU kann auch an sein, ohne dass Windows DMA-Schutz meldet, „unbekannt“ ist dort also kein Problem. Kann Windows den Status der virtualisierungsbasierten Sicherheit nicht melden, gilt die Speicherintegrität als unbekannt, nicht als aus.

## So behebst du es
1. **BitLocker:** Ist BitLocker oder die Geräteverschlüsselung an, setze den Schutz vorher aus (Start > **BitLocker verwalten** > **Schutz anhalten**) oder halte den Wiederherstellungsschlüssel bereit. Oft ist er in deinem Microsoft-Konto gespeichert. Nach einem BIOS-Update oder einer Änderung am TPM oder an der Startkonfiguration kann Windows beim nächsten Start danach fragen [15][16].
::: if mbr
2. **UEFI und GPT:** Der Systemdatenträger nutzt MBR. Prüfe ihn in einer als Administrator geöffneten Eingabeaufforderung mit `mbr2gpt /validate /allowFullOS` und wandle ihn danach mit `mbr2gpt /convert /allowFullOS` um. Der BitLocker-Schutz muss dafür angehalten sein. Stelle das BIOS danach auf reines UEFI (CSM aus), sonst startet Windows nicht [12]. Sichere vorher deine Daten.
:::
::: ifnot mbr
2. **UEFI:** Schalte im BIOS **CSM** (Legacy-Start) ab, damit der PC im reinen UEFI-Modus startet.
:::
3. **Secure Boot:** Stelle im BIOS auf der Seite für Start oder Sicherheit Secure Boot auf **Enabled** (bei ASUS-Mainboards: OS Type **Windows UEFI mode**). Manche Mainboards brauchen vorher „Install default Secure Boot keys“.
4. **TPM 2.0:** Schalte im BIOS **Intel PTT** oder **AMD fTPM** ein, oft unter „Security“ oder „Trusted Computing“ [5].
5. **Virtualisierung:** Schalte im BIOS **Intel Virtualization Technology (VT-x)** bzw. **SVM Mode** (AMD) ein. Speicherintegrität und VBS brauchen sie [6][13].
6. **Speicherintegrität und VBS:** Windows-Sicherheit > Gerätesicherheit > **Details zur Kernisolierung** > **Speicherintegrität**, danach neu starten [13][14]. Die Speicherintegrität ist eine Funktion von VBS, damit schaltest du also auch VBS ein [14]. Manche Treiber vertragen sich nicht mit der Speicherintegrität und können Fehler oder selten einen Bluescreen verursachen [14].
7. **IOMMU:** Schalte im BIOS **VT-d** (Intel) bzw. **IOMMU** / **AMD-Vi** (AMD) ein [5].

## So prüfst du die Behebung
Starte den Scan erneut. Jede geforderte Funktion sollte „Ja“ oder „Aktiv“ zeigen. Die Fehlercode-Seite des Anti-Cheats nennt die genaue Anforderung, die er prüft.

## Quellen
1. https://support.riotgames.com/riot/performance/vanguard-security-requirements
2. https://support.riotgames.com/en-us/riot/client/error-van-9003/
3. https://support.riotgames.com/en-us/riot/client/enable-tpm-20
4. https://support.faceit.com/hc/en-us/articles/23117375791772-How-can-I-check-if-I-need-to-make-any-changes-to-my-PC-before-using-the-FACEIT-Anti-Cheat
5. https://support.faceit.com/hc/en-us/articles/23117181142556-Windows-Security-Requirements-FAQ
6. https://support.faceit.com/hc/en-us/articles/22851956652956-Known-issues-with-Anti-Cheat-Requirements
7. https://www.ea.com/games/battlefield/battlefield-6/news/secure-boot-information
8. https://www.ea.com/news/ea-javelin-anticheat-2026-update
9. https://www.easy.ac/support/articles/additional-security-requirements
10. https://dev.epicgames.com/docs/epic-online-services/trust-and-safety/anti-cheat-interfaces/anti-cheat-interfaces
11. https://support.activision.com/articles/trusted-platform-module-and-secure-boot
12. https://learn.microsoft.com/de-de/windows/deployment/mbr-to-gpt
13. https://support.microsoft.com/de-de/windows/security/windows-security/device-security-in-the-windows-security-app
14. https://learn.microsoft.com/de-de/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity
15. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
16. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/operations-guide
