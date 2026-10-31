import type { Rich, Strings, TrustItem } from './en.ts';

const repo = 'https://github.com/jozuern/Tunero';

const de: Strings = {
  lang: 'de',
  meta: {
    title: 'Tunero, der PC-Optimierer für Windows 11',
    description:
      'Tunero prüft deinen Windows-11-PC auf das, was Spiele bremst, erklärt jede Änderung mit Quellen und macht alles rückgängig. Kostenlos, quelloffen, signiert.',
    classicTitle: 'Tunero, der PC-Optimierer für Windows 11 (klassische Seite)',
  },
  skip: 'Zur klassischen Seite',
  noscript: 'Der Demo-PC braucht JavaScript. Alles über Tunero steht auch auf der klassischen Seite:',
  skipContent: 'Zum Inhalt',
  notFound: {
    title: 'Seite nicht gefunden',
    body: 'Diese Adresse gibt es auf tunero.dev nicht.',
    home: 'Zur Startseite',
  },
  demo: {
    notSet: 'nicht gesetzt',
    display: 'Bildschirm 1',
    refreshWhere: 'Bildschirm 1: Refresh-Rate',
    power: {
      maxState: 'Energiesparplan, Netzbetrieb: Maximaler Leistungszustand des Prozessors',
      boostMode: 'Energiesparplan, Netzbetrieb: Leistungssteigerungsmodus für Prozessoren',
      boostOff: 'Deaktiviert',
      boostOn: 'Aggressiv',
    },
    startup: ['Spiele-Launcher', 'Chat', 'RGB-Steuerung', 'Cloud-Sync', 'Updater', 'Musik', 'Zubehör-Tool', 'Screenshot-Tool', 'Sprachchat'],
  },
  classic: {
    heading: 'Tunero',
    claim: 'Gebaut, damit du nur noch einen PC-Optimierer brauchst.',
    lead: 'Tunero sucht auf deinem PC, was deine Spiele FPS kostet, und behebt, was es kann. Es ist gebaut, um die Tweak-Tools, Debloat-Skripte, Autostart-Manager und Aufräumprogramme zu ersetzen, die du sonst zusammensuchst. Jede Änderung wird mit Quellen erklärt, und jede Einstellung, die es ändert, wird vorher gesichert und lässt sich rückgängig machen.',
    nav: 'Website',
    demo: 'Demo-PC öffnen',
    languages: 'Sprache',
    english: { short: 'EN', name: 'English' },
    german: { short: 'DE', name: 'Deutsch' },
  },
  download: {
    title: 'Download',
    button: 'Tunero.exe herunterladen',
    facts: (version) => `Version ${version} für Windows 11 25H2 oder neuer, x64. Auf Deutsch und Englisch. Freie Software unter der GPL‑3.0‑only.`,
    changes: 'Was neu ist',
    signed: 'Tunero.exe ist ab Version 1.2.3 signiert. Windows SmartScreen kann beim ersten Start trotzdem warnen.',
    signedLink: 'Warum',
    started: 'Der Download hat begonnen. Windows warnt beim Start?',
    startedLink: 'Hier steht, was zu tun ist',
    firstStart: 'Beim ersten Start bietet Tunero an, sich im Ordner Programme mit einem Eintrag im Startmenü zu installieren, oder dort zu bleiben, wo die Datei liegt.',
    managers: 'Mit einem Paketmanager',
    copy: 'Kopieren',
    copied: 'Kopiert',
    releases: 'Alle Versionen',
  },
  trust: {
    title: 'Warum du Tunero trauen kannst',
    intro: 'Tunero fragt nach Administratorrechten, weil die meisten Einstellungen, die es ändert, für den ganzen PC gelten. Das steht dahinter, und so prüfst du es selbst.',
    items: [
      {
        title: 'Von seinem Entwickler signiert',
        body: 'Tunero.exe ist ab Version 1.2.3 signiert, mit einem Certum-Zertifikat für Open-Source-Entwickler. Windows nennt den Herausgeber, Open Source Developer Joshua Zürn: Rechtsklick auf die exe, Eigenschaften > Digitale Signaturen, oder in PowerShell:',
        code: 'Get-AuthenticodeSignature Tunero.exe',
        link: { label: 'So werden Releases signiert (Englisch)', href: `${repo}#code-signing` },
      },
      {
        title: 'Quelloffen',
        body: 'Die App, ihre Tests, ihre Erklärseiten und diese Website liegen auf GitHub. Jedes Release baut der Release-Workflow auf GitHub aus dem Commit seines Versions-Tags, und das lässt sich prüfen:',
        code: 'gh attestation verify Tunero.exe --repo jozuern/Tunero',
        link: { label: 'Quellcode', href: repo },
      },
      {
        title: 'Freie Software unter der GPL‑3.0‑only',
        body: 'Du darfst es nutzen, untersuchen, ändern und weitergeben. Versionen, die du weitergibst, ob geändert oder nicht, müssen unter derselben Lizenz bleiben und ihren Quellcode mitbringen. Es gibt keine Bezahlversion, und eine Unterstützung schaltet nichts frei.',
        link: { label: 'Lizenz (Englisch)', href: `${repo}/blob/main/LICENSE` },
      },
      {
        title: 'Keine Telemetrie',
        body: 'Keine Telemetrie, kein Konto, keine Werbung, kein Dienst und kein Symbol im Infobereich. Ins Internet geht die App nur für Funktionen, die du startest oder einschaltest: die Update-Prüfung, eine VirusTotal-Abfrage mit deinem eigenen Schlüssel, den DNS-Benchmark, App-Installationen und den Windows-Download für das Installationsmedium.',
        link: { label: 'Alles, was die App sendet, und an wen', href: `${repo}/blob/main/docs/PRIVACY.md#datenschutz` },
      },
      {
        title: 'Jede Einstellung, die es ändert, lässt sich rückgängig machen',
        body: 'Jede Einstellung wird gesichert, bevor sie sich ändert, und nach dem Anwenden geprüft, und vor der ersten Änderung entsteht ein Wiederherstellungspunkt, wenn der Computerschutz eingeschaltet ist. Die Seite Änderungen listet jede Änderung mit „Rückgängig“, auch nach einem Neustart oder einem Update der App, und „Deinstallieren“ bietet an, vorher alles rückgängig zu machen. Entfernte Apps und gelöschte Dateien sind die Ausnahme, und das sagt die App vorher.',
      },
      {
        title: 'Den Download prüfen',
        body: 'Vergleiche den SHA-256 von Tunero.exe mit der .sha256-Datei auf der Release-Seite. PowerShell zeigt ihn mit:',
        code: 'Get-FileHash Tunero.exe',
        link: { label: 'Release-Seite', href: `${repo}/releases/latest` },
      },
      {
        title: 'Was es mit Absicht weglässt',
        body: 'Manche beliebten Tweaks gibt es gar nicht, weil sie schaden oder nichts ändern, etwa Echtzeitpriorität für Spiele, abgeschalteter Schutz vor Spectre und Meltdown oder Registry-Reinigung. Suchst du in der App danach, sagt sie dir, warum.',
        link: { label: 'Die ganze Liste, mit Quellen (Englisch)', href: `${repo}/blob/main/docs/not-included.md` },
      },
    ] satisfies TrustItem[] as TrustItem[],
  },
  board: {
    title: 'Eine 5 gibt es nur für Probleme, die der Scan findet. Für keinen Tweak.',
    body: (tweaks, matter) => [
      'Prüfungen und Tweaks teilen sich eine Skala für Spiele, von 0 bis 5. Den größten Unterschied machen Probleme, die der Scan findet, etwa ein Monitor unter seiner Bildwiederholrate oder eine CPU, die beim Basistakt festhängt.',
      `Von den ${tweaks} Tweaks können ${matter} bei Spielen eine Rolle spielen, manche nur auf manchen PCs: „CPU-Turbo wiederherstellen“ hat eine 4, wo der Energiesparplan die CPU beim Basistakt hält, und überall sonst eine 0. Die App bewertet jeden Tweak für den PC, auf dem sie läuft, und zeigt die Wertung neben jeder Prüfung und jedem Tweak als Zahl und fünf Balken.`,
    ],
    all: (checks, tweaks) => `Hier stehen alle ${checks} Prüfungen und alle ${tweaks} Tweaks, jeweils in der Zeile ihrer höchsten Wertung.`,
    rowLabel: (level) => `Wirkung ${level}`,
    checks: 'Probleme, die der Scan findet',
    top: 'Er behebt sie oder sagt dir, wie.',
    tweaks: 'Tweaks',
    measured: 'gemessen',
    disputed: 'umstritten',
    some: (base) => `auf manchen PCs, sonst ${base}`,
    also: (levels) => `je nach Befund auch ${levels}`,
    zeroNamed: 'Beliebte Gaming-Tweaks mit umstrittenen Belegen. Angeboten, mit 0 bewertet, und ihre Seiten erklären den Streit.',
    zeroRest: (count) => `Dazu ${count} weitere, die meisten für Datenschutz, den Browser und Windows. Sie versprechen keine FPS.`,
    unrated: 'Nicht auf der Skala',
    access: 'Was Kernel-Anti-Cheats vor dem Spielstart prüfen. Nicht Teil der Wirkungsbewertung.',
    critical: 'Als „Kritisch“ markiert statt bewertet, weil es um die Stabilität des Prozessors geht.',
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
        a: '„Deinstallieren“ in den Einstellungen bietet an, vorher jede Änderung rückgängig zu machen. Dann entfernt es die exe, seinen Datenordner und seinen Eintrag im Startmenü, bei einer installierten Kopie auch ihren Ordner unter Programme und ihren Eintrag unter Installierte Apps. „Deinstallieren“ in den Windows-Einstellungen unter Apps macht dasselbe. Bei einer Kopie aus Chocolatey oder Scoop bietet „Deinstallieren“ in den Einstellungen das Rückgängigmachen genauso an und führt dann choco uninstall oder scoop uninstall für dich aus. Der Paketmanager allein lässt deine Änderungen bestehen und behält den Datenordner mit ihren Sicherungen: Wenn Tunero ihn nicht starten kann und dir stattdessen den Befehl zeigt, oder wenn du direkt mit ihm deinstallierst, mach deine Änderungen vorher auf der Seite Änderungen rückgängig.',
      },
    ],
  },
  footer: {
    label: 'Projektlinks',
    license: 'Freie Software unter der GPL‑3.0‑only.',
    version: (version) => `Tunero ${version}.`,
    links: {
      releases: 'Versionen',
      changelog: 'Änderungen',
      source: 'Quellcode',
      kofi: 'Auf Ko-fi unterstützen',
      sponsors: 'GitHub Sponsors',
      privacy: 'Datenschutz',
      legal: 'Impressum',
    },
  },
  privacy: {
    title: 'Datenschutz',
    metaTitle: 'Datenschutz, Tunero',
    description: 'Datenschutzerklärung für tunero.dev.',
    intro: [
      'Diese Erklärung gilt für die Website tunero.dev. Was die App Tunero selbst sendet, steht in der ',
      { text: 'Datenschutzerklärung der App', href: `${repo}/blob/main/docs/PRIVACY.md#datenschutz` },
      '.',
    ] satisfies Rich as Rich,
    controller: 'Verantwortlicher',
    email: 'E-Mail',
    sections: (email) => [
      {
        heading: 'Kurz gesagt',
        paragraphs: [
          ['Diese Website setzt keine Cookies, nutzt keine Analyse und keine Werbung und lädt keine Schriften, Skripte oder Bilder von anderen Servern. Sie besteht aus statischen Dateien.'],
          ['Der Demo-PC auf der Startseite merkt sich den Farbmodus, den du wählst, ob du seine Animationseffekte und seinen Ton ein- oder ausgeschaltet hast, ob du seine Startanimation schon gesehen hast, und was du an ihm geändert hast, im Speicher deines Browsers. Das bleibt in deinem Browser und wird nirgendwohin gesendet. Dafür ist nach § 25 Abs. 2 Nr. 2 TDDDG keine Einwilligung nötig, weil die Demo diese Angaben braucht, um so zu funktionieren, wie du sie eingestellt hast; du kannst sie jederzeit in den Websitedaten deines Browsers löschen.'],
        ],
      },
      {
        heading: 'Hosting bei GitHub Pages',
        paragraphs: [
          [
            'Die Website liegt bei GitHub Pages, einem Dienst der GitHub, Inc., 88 Colin P. Kelly Jr. St., San Francisco, CA 94107, USA. Beim Aufruf einer Seite verarbeitet GitHub deine IP-Adresse, um die Seite auszuliefern, und speichert sie laut eigener Angabe aus Sicherheitsgründen. Rechtsgrundlage ist Art. 6 Abs. 1 lit. f DSGVO: ich habe ein berechtigtes Interesse daran, die Website sicher und zuverlässig bereitzustellen. GitHub nimmt am EU-US Data Privacy Framework teil und nutzt außerdem die Standardvertragsklauseln der EU-Kommission. Mehr dazu in der ',
            { text: 'Datenschutzerklärung von GitHub', href: 'https://docs.github.com/de/site-policy/privacy-policies/github-general-privacy-statement' },
            '.',
          ],
        ],
      },
      {
        heading: 'Domain und E-Mail bei Cloudflare',
        paragraphs: [
          [
            `Die Domain tunero.dev wird über Cloudflare, Inc. verwaltet. Cloudflare beantwortet die DNS-Abfragen für die Domain, liefert aber die Seiten nicht aus. Schreibst du an ${email}, leitet Cloudflare Email Routing die Nachricht an mein Postfach weiter. Ich nutze deine Angaben nur, um dir zu antworten (Art. 6 Abs. 1 lit. b und f DSGVO), und lösche die Nachricht, wenn sie erledigt ist und keine Aufbewahrungspflicht besteht. Mehr dazu in der `,
            { text: 'Datenschutzerklärung von Cloudflare', href: 'https://www.cloudflare.com/de-de/privacypolicy/' },
            '.',
          ],
        ],
      },
      {
        heading: 'Links zu anderen Seiten',
        paragraphs: [
          ['Der Download, der Quellcode und die Versionen liegen auf GitHub, die Unterstützung läuft über Ko-fi. Folgst du einem dieser Links, gilt dort die Datenschutzerklärung des jeweiligen Anbieters.'],
        ],
      },
      {
        heading: 'Deine Rechte',
        paragraphs: [
          [
            `Du hast das Recht auf Auskunft, Berichtigung, Löschung und Einschränkung der Verarbeitung, auf Datenübertragbarkeit und auf Widerspruch gegen eine Verarbeitung, die auf berechtigtem Interesse beruht (Art. 15 bis 21 DSGVO). Schreib dazu an ${email}. Du kannst dich außerdem bei einer Datenschutz-Aufsichtsbehörde beschweren, zum Beispiel beim `,
            { text: 'Landesbeauftragten für den Datenschutz und die Informationsfreiheit Baden-Württemberg', href: 'https://www.baden-wuerttemberg.datenschutz.de/' },
            '.',
          ],
        ],
      },
    ],
  },
  legal: {
    title: 'Impressum',
    metaTitle: 'Impressum, Tunero',
    description: 'Anbieterkennzeichnung für tunero.dev.',
    intro: [] satisfies Rich as Rich,
    operator: 'Angaben gemäß § 5 DDG',
    contact: 'Kontakt',
    email: 'E-Mail',
    responsible: 'Verantwortlich für den Inhalt nach § 18 Abs. 2 MStV',
  },
};

export default de;
