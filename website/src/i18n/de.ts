// Deutsche Texte. Gleiche Stimme wie die App: du, einfache Wörter, echte Beispiele, keine Gedankenstriche; starke Aussagen nur, wo sie stimmen (DESIGN.md).
import type { Copy } from './types';

const de: Copy = {
  lang: 'de',
  meta: {
    title: 'Tunero: PC-Optimierer für Windows 11',
    description:
      'PC-Optimierer für Windows 11, gebaut, damit du nur noch einen brauchst. Tunero findet, was deine Spiele FPS kostet, erklärt jede Änderung mit Quellen und kann jede Einstellung rückgängig machen, die es ändert. Kostenlos und quelloffen.',
  },
  nav: { label: 'Hauptnavigation', download: 'Download', source: 'Quellcode', support: 'Unterstützen', questions: 'Fragen', other: 'English', skip: 'Zum Inhalt' },
  hero: {
    title: 'Gebaut, damit du nur noch einen PC-Optimierer brauchst.',
    lead: 'Tunero sucht auf deinem PC, was deine Spiele FPS kostet, und behebt, was es kann. Es ist gebaut, um die Tweak-Tools, Debloat-Skripte, Autostart-Manager und Aufräumprogramme zu ersetzen, die du sonst zusammensuchst. Jede Änderung wird mit Quellen erklärt, und jede Einstellung, die es ändert, wird vorher gesichert und lässt sich rückgängig machen.',
    download: 'Tunero.exe herunterladen',
    winget: 'oder mit winget',
    copy: 'Kopieren',
    copied: 'Kopiert',
    facts: (v) => `Version ${v} für Windows 11 25H2 oder neuer, x64. Auf Deutsch und Englisch. Freie Software unter der GPL-3.0-only.`,
    changes: 'Was neu ist',
    preview: 'Tunero.exe ist ab Version 1.2.3 signiert. Windows SmartScreen kann beim ersten Start trotzdem warnen.',
    previewLink: 'Warum',
    started: 'Der Download hat begonnen. Windows warnt beim Start?',
    startedLink: 'Hier steht, was zu tun ist',
    shotCaption: 'Die Übersicht nach einem Scan, auf einem Test-PC in einer virtuellen Maschine.',
    shotAlt:
      'Die Übersicht von Tunero: das Urteil des Scans, die Eignung in einer Zeile, die Ergebnisse mit ihrer Wirkung und der Anfang der empfohlenen Änderungen.',
  },
  impact: {
    title: 'Eine 5 gibt es nur für Probleme, die der Scan findet. Für keinen Tweak.',
    body: (n, matter) => [
      'Prüfungen und Tweaks teilen sich eine Skala für Spiele, von 0 bis 5. Den größten Unterschied machen Probleme, die der Scan findet, etwa ein Monitor unter seiner Bildwiederholrate oder eine CPU, die beim Basistakt festhängt.',
      `Von den ${n} Tweaks können ${matter} bei Spielen eine Rolle spielen, manche nur auf manchen PCs: „CPU-Turbo wiederherstellen“ hat eine 4, wo der Energiesparplan die CPU beim Basistakt hält, und überall sonst eine 0. Die App bewertet jeden Tweak für den PC, auf dem sie läuft, und zeigt die Wertung neben jeder Prüfung und jedem Tweak als Zahl und fünf Balken.`,
    ],
    top: 'Probleme, die der Scan findet. Er behebt sie oder sagt dir, wie.',
    topRow: (n) => `Wirkung 5: ${n} Probleme, die der Scan findet`,
    measured: 'gemessen',
    disputed: 'umstritten',
    some: (base) => `auf manchen PCs, sonst ${base}`,
    zeroNamed: 'Beliebte Gaming-Tweaks mit umstrittenen Belegen. Angeboten, mit 0 bewertet, und ihre Seiten erklären den Streit.',
    zeroRest: (n) => `Dazu ${n} weitere, die meisten für Datenschutz, den Browser und Windows. Sie versprechen keine FPS.`,
    row: (level, count) => `Wirkung ${level}: ${count} ${count === 1 ? 'Tweak' : 'Tweaks'}`,
  },
  scan: {
    title: 'Der Scan sucht, was dich wirklich FPS kostet',
    body: (n) => [
      `${n} Prüfungen, die nur lesen, suchen nach Problemen, die FPS oder flüssiges Spielen kosten. Kann eine Prüfung einen Wert nicht lesen, sagt sie „unbekannt“ und gibt keinen Rat.`,
      'Ein Profil legt fest, was empfohlen wird und welche Wirkung zählt: Gaming, Laptop-Gaming, Akku, Büro, Leise und kühl oder Älterer PC. Von sich aus ändert es nichts.',
    ],
    list: (n) => `Die ${n} Prüfungen`,
  },
  tweak: {
    title: 'Jede Änderung wird vorher erklärt, und jede Einstellung lässt sich rückgängig machen',
    intro:
      'Jede Prüfung und jeder Tweak hat eine Seite auf Deutsch und Englisch: was er tut, was er schreibt, warum er helfen kann, wie belastbar die Belege sind, und die Quellen. Die App zeigt sie neben dem Schalter. Das ist die Seite zum Ausschalten der Mausbeschleunigung:',
    docLabel: 'Erklärseite in der App',
    impact: (n) => `Wirkung ${n} von 5`,
    changes: 'Was sich ändert',
    columns: ['Wert', 'Jetzt', 'Neu'],
    caption: '„Jetzt“ zeigt die Standardwerte von Windows. Die App zeigt die Werte deines PCs.',
    show: ['Zusammenfassung', 'So funktioniert es', 'Belege', 'Nachteile & Risiken'],
    sourcesLabel: 'Quellen',
    disputed: {
      intro: (title) => `Wo die Belege nicht tragen, sagt die Seite das. Die Seite zu ${title}, einem beliebten Tweak:`,
      link: 'Die ganze Seite lesen',
    },
    stepsTitle: 'Was beim Anwenden passiert',
    steps: [
      { name: 'Wiederherstellungspunkt', text: 'Vor der ersten Änderung, wenn der Computerschutz eingeschaltet ist.' },
      { name: 'Sicherung', text: 'Jeder Wert wird gesichert, bevor er sich ändert.' },
      { name: 'Prüfung', text: 'Nach dem Anwenden wird jede Änderung geprüft.' },
      { name: 'Rückgängig', text: 'Die Seite Änderungen listet jede Änderung mit „Rückgängig“, auch nach einem Neustart oder einem Update der App.' },
    ],
    body: [
      'Nach einem Windows-Update zeigt die App, welche Änderungen Windows zurückgesetzt hat, und bietet an, sie erneut anzuwenden. Entfernte Apps und gelöschte Dateien sind die Ausnahme, und das sagt die App, bevor sie etwas entfernt.',
      'Änderungen, die Sicherheitsfunktionen ausschalten oder die Startkonfiguration ändern, bleiben im Expertenmodus, und keine davon wird je automatisch empfohlen. Ein Tweak, der auf echter Hardware noch nicht erprobt ist, trägt das Abzeichen „Noch nicht auf einem echten PC getestet“ und ist nie Teil von „Empfohlene anwenden“.',
    ],
    shotAlt:
      'Die Seite Tweaks von Tunero, gewählt ist „Mausbeschleunigung aus“: Gruppen von Tweaks mit ihrer Wirkung und Schaltern und die Erklärung im Detailbereich.',
    shotCaption: 'Derselbe Tweak auf der Seite Tweaks.',
  },
  left: {
    title: 'Was es weglässt, und warum',
    intro: 'Manche beliebten Tweaks gibt es gar nicht, weil sie schaden oder nichts ändern. Suchst du in der App danach, sagt sie dir, warum.',
    rows: [
      {
        tweak: 'Echtzeitpriorität für Spiele',
        why: 'Microsoft warnt, dass ein Prozess mit Echtzeitpriorität Threads des Betriebssystems verdrängt und die Maus nicht mehr reagieren kann.',
        source: 'https://learn.microsoft.com/de-de/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass',
      },
      {
        tweak: 'Schutz vor Spectre und Meltdown aus',
        why: 'Microsoft hat auf den Prozessoren, die Windows 11 unterstützt, Verluste im einstelligen Prozentbereich gemessen, die die meisten nicht bemerken. Ein echter Sicherheitsverlust für einen Gewinn, den man nicht spürt.',
        source: 'https://www.microsoft.com/en-us/security/blog/2018/01/09/understanding-the-performance-impact-of-spectre-and-meltdown-mitigations-on-windows-systems/',
      },
      {
        tweak: 'Win32PrioritySeparation = 0x26',
        why: 'Das ist genau der Wert, den Windows für „Optimale Leistung anpassen für: Programme“ schon schreibt. Der Tweak ändert unter Windows 11 nichts.',
        source: 'https://learn.microsoft.com/en-us/previous-versions/cc976120(v=technet.10)',
      },
      {
        tweak: 'IPv6 ganz ausschalten',
        why: 'Microsoft rät davon ab, weil einige Windows-Komponenten dann nicht mehr funktionieren können. Die App bietet stattdessen „IPv4 vor IPv6 bevorzugen“.',
        source: 'https://learn.microsoft.com/de-de/troubleshoot/windows-server/networking/configure-ipv6-in-windows',
      },
      {
        tweak: 'Globale TCP-Tweaks mit netsh',
        why: 'Sie ändern jede Verbindung des PCs, und keine Messung zeigt einen Gewinn in Spielen. Der Ping in Onlinespielen hängt vor allem vom Weg zum Server ab.',
      },
      {
        tweak: 'Registry reinigen',
        why: 'Schlüssel, die ein Deinstallationsprogramm zurücklässt, belegen keinen messbaren Platz, und keine Messung zeigt, dass sie Windows bremsen. Ein falscher Treffer macht Programme kaputt, die sie mitnutzen.',
      },
    ],
    sourceLink: 'Quelle',
    all: 'Die ganze Liste, mit Quellen (Englisch)',
  },
  areas: {
    title: 'Gebaut, um einen Ordner voller Tools zu ersetzen',
    intro:
      'Tunero vereint, was man sonst aus Tweak-Tools, Debloat-Skripten, Autostart-Managern, Aufräumprogrammen und Systemprogrammen zusammensucht, und lässt weg, was nichts bringt. Was es anderen Tools überlässt, etwa einem Treiber-Entferner, steht in seinem README unter „What it does not replace“. Das sind seine wichtigsten Seiten, gruppiert wie in der App.',
    groups: (c) => [
      {
        name: '',
        items: [['Übersicht', `${c.checks} Prüfungen, die finden, was FPS kostet, mit Behebung oder den Schritten für BIOS und Treiber.`]],
      },
      {
        name: 'Optimieren',
        items: [
          ['Tweaks', `${c.tweaks} dokumentierte Einstellungen für Spiele, Datenschutz, den Browser, Windows und Windows Update, jede mit ihren Belegen.`],
          ['Grafik & DNS', 'NVIDIA-Treibereinstellungen, maximale Leistung nur für die Spiele, die du auswählst, und ein DNS-Benchmark.'],
          ['Autostart', 'Alles, was mit Windows startet, mit Signaturprüfung und Schnappschüssen zum Vergleichen.'],
          ['Dienste & Aufgaben', 'Starttypen, mit dem, was jeder Dienst tut.'],
        ],
      },
      {
        name: 'Aufräumen',
        items: [
          ['Apps entfernen', `${c.apps} geprüfte vorinstallierte Apps und Hersteller-Apps, dazu Programme mit eigenem Deinstallationsprogramm.`],
          ['Bereinigen', `${c.cleanup} Kategorien temporärer Dateien, Caches und Reste, ohne Verknüpfungen zu folgen.`],
          ['Speicher', 'Ein Laufwerk als Ordnerbaum neben einer Treemap, der Platz nach Dateityp, die größten Dateien und doppelte Dateien, gelesen nur, wenn du es willst. Was du löschst, kommt in den Papierkorb.'],
        ],
      },
      {
        name: 'Pflegen und prüfen',
        items: [
          ['Installieren & aktualisieren', 'Apps mit winget installieren und aktualisieren, und sehen, wie alt die Treiber sind.'],
          ['Tools', 'Windows-Features, Netzwerkreparaturen, Windows-Update-Reparatur, SFC und DISM.'],
          ['Zustand', 'Ein Frametime-Benchmark mit PresentMon, eine Drosselungsprüfung und der Zustand der Laufwerke.'],
        ],
      },
      {
        name: '',
        items: [['Änderungen', 'Jede Änderung mit „Rückgängig“, dazu ein Export, um deine Änderungen auf einen anderen PC oder eine Neuinstallation zu übertragen.']],
      },
    ],
  },
  privacy: {
    title: 'Ins Internet geht es nur, wenn du es willst',
    body: [
      'Keine Telemetrie, kein Konto, keine Werbung, kein Dienst und kein Symbol im Infobereich: Es läuft, wenn du es startest, oder einmal nach der Anmeldung, wenn du in den Einstellungen das erneute Anwenden deiner Änderungen einschaltest. Die Installation im Ordner Programme ist freiwillig.',
      'Die Update-Prüfung ist aus, bis du sie einschaltest. Eine VirusTotal-Abfrage braucht deinen eigenen Schlüssel und sendet Hashes von Dateien, nie die Dateien. Der DNS-Benchmark und App-Installationen laufen, wenn du sie startest.',
    ],
    link: 'Alles, was die App sendet, und an wen',
  },
  support: {
    title: 'Tunero unterstützen',
    body: [
      'Tunero ist kostenlos, ohne Werbung, ohne Konto und ohne Bezahlversion. Wenn es dir hilft und du etwas zurückgeben möchtest, kannst du die Entwicklung auf Ko-fi oder über GitHub Sponsors unterstützen. Fehlerberichte und Ideen helfen genauso.',
    ],
    kofi: 'Auf Ko-fi unterstützen',
    sponsors: 'Auf GitHub sponsern',
    note: 'Eine Unterstützung schaltet nichts frei: Jede Funktion steckt in der kostenlosen exe.',
  },
  install: {
    title: 'Hol dir Tunero',
    steps: [
      { name: 'Herunterladen', text: 'Die exe der neuesten Version.' },
      { name: 'Prüfen', text: 'Rechtsklick, Eigenschaften > Digitale Signaturen: Dort steht Open Source Developer Joshua Zürn. Oder vergleiche ihren SHA-256 mit der .sha256-Datei auf der Release-Seite.' },
      { name: 'Starten', text: 'Beim ersten Start bietet sie an, sich im Ordner Programme mit einem Eintrag im Startmenü zu installieren, oder dort zu bleiben, wo sie liegt (Von hier ausführen).' },
    ],
    managers: 'Mit einem Paketmanager',
    releases: 'Alle Versionen',
  },
  faq: {
    title: 'Fragen',
    items: [
      {
        id: 'schneller',
        q: 'Laufen meine Spiele damit schneller?',
        a: 'Wenn der Scan ein echtes Problem findet, oft deutlich: Ein Monitor auf 60 Hz, Arbeitsspeicher ohne sein XMP- oder EXPO-Profil oder ein Prozessor, der auf seinem Basistakt festhängt, kostet viel FPS, und Tunero behebt das oder sagt dir, wie. Die meisten Tweaks betreffen Datenschutz, den Browser und Windows und sind für Spiele mit 0 bewertet; die, die etwas bringen können, werden für deinen PC bewertet, und jede Seite sagt, wie belastbar die Belege sind. Auf der Seite Zustand gibt es einen Frametime-Benchmark, damit du den Unterschied in deinen eigenen Spielen messen kannst.',
      },
      {
        id: 'smartscreen',
        q: 'Warum warnt Windows beim Start?',
        a: 'SmartScreen warnt vor Programmen, die noch nicht genug Leute gestartet haben, ob signiert oder nicht. Ab 1.2.3 ist Tunero.exe signiert: Unter „Weitere Informationen“ steht dann der Herausgeber, Open Source Developer Joshua Zürn, statt „Unbekannter Herausgeber“, und spätere Releases mit demselben Zertifikat können auf dessen Ruf aufbauen. Solange es warnt, klicke auf „Weitere Informationen“ und dann auf „Trotzdem ausführen“. Um den Download zu prüfen, sieh unter Eigenschaften > Digitale Signaturen nach oder vergleiche seinen SHA-256 mit der .sha256-Datei auf der Release-Seite.',
      },
      {
        id: 'smartappcontrol',
        q: 'Was, wenn Smart App Control es blockiert?',
        a: 'Smart App Control ist strenger als SmartScreen: Es blockiert unsignierte Apps, die Microsoft noch nicht kennt, und seine Meldung hat keinen Knopf „Trotzdem ausführen“. Apps mit gültiger Signatur lässt es starten, außer Microsoft stuft sie als unsicher ein, Tunero ab 1.2.3 sollte es also nicht aufhalten; hat es eine ältere Version blockiert, lade das aktuelle Release. Du kannst es auch unter Windows-Sicherheit > App- & Browsersteuerung > Einstellungen für Smart App Control ausschalten; mit aktuellen Windows-11-Updates lässt es sich dort auch wieder einschalten.',
      },
      {
        id: 'admin',
        q: 'Warum braucht es Administratorrechte?',
        a: 'Die meisten Einstellungen, die es ändert, gelten für den ganzen PC. Sein Datenordner ist für Administratoren gesperrt.',
      },
      {
        id: 'home',
        q: 'Läuft es unter Windows 11 Home?',
        a: 'Ja. Manche Tweaks, die Gruppenrichtlinien brauchen, gibt es nur unter Pro, Enterprise und Education. Home ist bisher weniger getestet als Pro.',
      },
      {
        id: 'entfernen',
        q: 'Kann ich es vollständig entfernen?',
        a: '„Deinstallieren“ in den Einstellungen bietet an, vorher jede Änderung rückgängig zu machen. Dann entfernt es die exe, seinen Datenordner und seinen Eintrag im Startmenü, bei einer installierten Kopie auch ihren Ordner unter Programme und ihren Eintrag unter Installierte Apps. „Deinstallieren“ in den Windows-Einstellungen unter Apps macht dasselbe. Eine Kopie aus Scoop entfernst du mit scoop uninstall tunero.',
      },
    ],
  },
  footer: {
    label: 'Projektlinks',
    license: (v) => `Tunero ${v}. Freie Software unter der GPL-3.0-only.`,
    links: { releases: 'Versionen', changelog: 'Änderungen', source: 'Quellcode', privacy: 'Datenschutz', imprint: 'Impressum', kofi: 'Auf Ko-fi unterstützen', sponsors: 'GitHub Sponsors' },
    privacyHref: '/de/datenschutz/',
    imprintHref: '/de/impressum/',
  },
};

export default de;
