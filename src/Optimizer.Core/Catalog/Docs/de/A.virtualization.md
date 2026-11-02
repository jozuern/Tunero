# Hardware-Virtualisierung im BIOS aus

## Zusammenfassung
::: variant antiCheat
Die Hardware-Virtualisierung ist im BIOS aus. Speicherintegrität und VBS brauchen sie, und installierte Anti-Cheats können eines davon verlangen ({{antiCheats}}). Spiele starten dann womöglich nicht.
:::
::: variant off
Die Hardware-Virtualisierung (Intel VT-x oder AMD-V) ist im BIOS aus. Speicherintegrität sowie Linux- oder Android-Apps unter Windows brauchen sie.
:::
::: variant default
Prüft, ob die Hardware-Virtualisierung (Intel VT-x oder AMD-V) im BIOS an ist. Speicherintegrität braucht sie.
:::

## Warum das wichtig ist
Die Speicherintegrität (Kernisolierung) ist eine Funktion der virtualisierungsbasierten Sicherheit (VBS) und arbeitet in einer abgeschotteten Umgebung, die Windows mit Hardware-Virtualisierung erzeugt [8]. Dafür muss die Virtualisierung im UEFI oder BIOS eingeschaltet sein [1]. Manche Anti-Cheats prüfen diese Funktionen: Riot Vanguard kann je nach PC die Speicherintegrität verlangen [2], und FACEIT verlangt VBS zusammen mit IOMMU, schrittweise in Wellen, und bittet manche Spieler, die Speicherintegrität einzuschalten [3][7]. Außerdem kann Windows mit Virtualisierung andere Betriebssysteme ausführen, etwa Linux- oder Android-Apps [4].

Die Virtualisierung im BIOS einzuschalten ändert die Spieleleistung für sich allein nicht. Ob du danach die Speicherintegrität einschaltest, ist eine eigene Entscheidung.

## Wie wir es erkennen
Wir lesen aus Windows (WMI), ob der Prozessor Virtualisierung unterstützt (`Win32_Processor.VMMonitorModeExtensions`), ob die Firmware sie eingeschaltet hat (`VirtualizationFirmwareEnabled`) [5] und ob ein Hypervisor vorhanden ist (`Win32_ComputerSystem.HypervisorPresent`) [6]. Auf manchen PCs erscheinen die CPU-Werte als aus, während der Windows-Hypervisor läuft. Ein laufender Hypervisor zählt deshalb als „an“.

## So behebst du es
1. **BitLocker:** Prüfe, bevor du BIOS-Einstellungen änderst, ob BitLocker oder die Geräteverschlüsselung an ist. Wenn ja, setze den Schutz vorher aus (Start > **BitLocker verwalten** > **Schutz anhalten**) oder halte den Wiederherstellungsschlüssel bereit. Oft ist er in deinem Microsoft-Konto gespeichert. Nach einem BIOS-Update oder einer Änderung am TPM oder an der Startkonfiguration kann Windows beim nächsten Start danach fragen [9][10].
2. Starte neu ins UEFI: **Einstellungen > System > Wiederherstellung**, neben **Erweiterter Start** auf **Jetzt neu starten**, dann **Problembehandlung > Erweiterte Optionen > UEFI-Firmwareeinstellungen** (je nach Version „UEFI-Einstellungen“) **> Neu starten** [4]. Bei vielen PCs öffnet es sich auch, wenn du beim Start **Entf** oder **F2** drückst.
::: if intel
3. Schalte **Intel Virtualization Technology (VT-x)** ein, bei manchen Mainboards **Intel (VMX) Virtualization Technology** genannt, meist auf der CPU- oder Advanced-Seite [4].
:::
::: ifnot intel
3. Schalte **AMD Virtualization (AMD-V)** ein [4]. Viele Mainboards nennen es **SVM Mode** [7]; suche nach „SVM“ oder „Virtualization“, wenn das Menü anders aussieht.
:::
4. Speichere und beende (meist **F10**).

## So prüfst du die Behebung
Starte den Scan erneut. „Virtualisierung (Intel VT-x oder AMD-V) an“ sollte „Ja“ zeigen.

## Quellen
1. https://support.microsoft.com/de-de/windows/security/windows-security/device-security-in-the-windows-security-app
2. https://support.riotgames.com/riot/performance/vanguard-security-requirements
3. https://support.faceit.com/hc/en-us/articles/23117181142556-Windows-Security-Requirements-FAQ
4. https://support.microsoft.com/de-de/windows/experience/enable-virtualization-on-windows
5. https://learn.microsoft.com/de-de/windows/win32/cimwin32prov/win32-processor
6. https://learn.microsoft.com/de-de/windows/win32/cimwin32prov/win32-computersystem
7. https://support.faceit.com/hc/en-us/articles/22851956652956-Known-issues-with-Anti-Cheat-Requirements
8. https://learn.microsoft.com/de-de/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity
9. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
10. https://learn.microsoft.com/de-de/windows/security/operating-system-security/data-protection/bitlocker/operations-guide
