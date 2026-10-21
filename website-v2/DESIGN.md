# tunero.dev v2 design

v2 is a new site, built from scratch next to v1 (`website/`), which stays as it is and keeps serving tunero.dev until
the owner switches. The v1 design notes do not apply here. What still applies, because it is about the product and not
the look: nothing untrue (no invented numbers, no performance claims the sources do not carry), counts and catalog
facts read from the repository at build time, no telemetry, the copy rules (no en or em dashes, middle dots, ellipsis
characters, bullets or "(s)" plurals in visible text), English and German.

## Goal

Someone who spends 30 seconds on the site should have felt what the app does, remember the site, and trust the app
with full access to their PC. Someone who only wants the download gets it in one click from anywhere.

## Idea: fix this PC

The site is a Windows 11 style desktop running in the browser, and it is a neglected one. The visitor opens Tunero on
it, runs the scan, applies fixes and watches the desktop around the window get cleaner. Then they press Undo all and
watch it rewind. Doing it is the explanation; the rewind is the trust.

## The experience

0. **Boot.** About 2 s, first visit of a session only (remembered in `sessionStorage` with the demo state), skipped by any key or click and under
   `prefers-reduced-motion`. Black screen, the Tunero mark, then a few boot lines that are true and read at build
   time: version, the signature, the number of checks and tweaks. Hard cut to the desktop.
1. **The demo PC.** A desktop full of the clutter Tunero removes: toasts (cloud sync nag, "get more out of Windows"
   style suggestions, a feedback request), a crowded tray, recommendations and an account nag in Start, trending
   content in search, a long startup list, a restless wallpaper. A watermark in the corner, styled like the Windows
   test mode watermark: "Demo PC, Windows 11 Pro. Nothing here touches your computer." The demo PC is Pro because
   some of its tweaks are documented for Pro and up; a tweak that Microsoft supports only on Enterprise and Education,
   or whose effect it has not confirmed, is not in the demo. The Tunero icon on the desktop pulses until it is
   opened.
2. **Tunero.** A recreation of the real app in its Fluent look (navigation rail, cards, toggle switches): Overview,
   Scan, Tweaks, Changes. Check titles, ratings, summaries and the scan's check count come from the catalog. The scan
   walks through every check and finds the fixed demo set of problems: the display at 60 Hz of 144 and the processor
   turbo blocked by the power plan (both rated 5), plus many startup programs. Turbo is fixed by the tweak the catalog
   names for it, which on this PC carries the rating the catalog gives it when turbo is blocked (4, with the app's
   reason). As in the app, a rating of 0 shows no mark; the detail pane says "No FPS effect".
3. **Fixing.** Fix a finding, switch a tweak on, or turn on a whole category with the app's "Turn on" button. Each change shows what it writes (registry key and value
   from the catalog, or the service and its start type) and the desktop reacts at the same moment: the toast stops,
   the tray icon goes, the Start ad goes, the startup list shrinks, removed apps fly out of Start. Every desktop effect
   is something that tweak really does. No frame rates, no speed numbers.
4. **Undo.** Changes page, Undo all: the desktop rewinds change by change, back to the cluttered state.
5. **The desktop is the navigation.**
   - Taskbar: an Install Tunero button that is always there, and an "Exit to classic page" link.
   - Start button: the Tunero mark (not the Windows logo). Start holds search, which finds Install, FAQ and Privacy.
   - Desktop icons: Tunero; a "Why trust it" folder in an Explorer window (code signature, source code, GPL-3.0, no
     telemetry, how undo works, checking the SHA-256, what it leaves out on purpose); Notepad with the changelog;
     Settings with language (EN, DE) and theme (light, dark), which switch the whole desktop.
   - Windows can be moved, minimized, maximized and closed; one open window at a time in the compact layout.

### Set pieces (WebGL)

- **Wallpaper:** a full screen fragment shader (raw WebGL2, no library). Its restlessness follows a single
  `clutter` value from 1 (demo state) to 0 (everything fixed), eased, so fixing calms it and undo stirs it up again.
  Neglect shows as muddy colour, grain, a wobble and faster motion, never as dropped frames or glitches: on a site
  about a faster PC, anything that looks like lag reads as the site being slow.
  Paused when the tab is hidden, rendered once and frozen under reduced motion, capped at 1.5 device pixel ratio.
  Without WebGL2 a static CSS background stands in.
- **The arrival:** on an untouched demo PC three notifications pile up at once, then come one at a time. A headline
  sits on the wallpaper like the clock of a lock screen, with "Watch Tunero fix it", "Fix it yourself" and a link to
  the comparison. A "This PC" widget in the corner shows the values the two big fixes change (the refresh rate counts
  up from 60 to 144 Hz, turbo goes from blocked to allowed) and the startup count.
- **Shatter:** what a change removes from the desktop (marked `data-effect` or `data-change`) bursts into specks of its
  own colours that stream into Tunero's Changes entry, with a ring of light in the wallpaper where it was
  (`particles.ts`, ripples in the shader).
- **Watch Tunero fix it** (`guide.ts`, plan in `lib/autoplay.ts`): a cursor clicks the real buttons page by page, the
  clicks a visitor would make (Fix, Turn on per group, each startup switch, Remove per app). The app has no fix-all
  button and the demo does not pretend it has one. Any key or click takes over. About 15 seconds.
- **The finale:** when nothing is left, the wallpaper blooms and a band of light sweeps across, and an end card counts
  what was done and offers Install, the comparison and Undo all.
- **Before/after:** a line the visitor drags across the whole screen; the shader splits the wallpaper, and the
  taskbar, Start and notifications are drawn twice from a view of the store that only shows (`comparisonView`).
- **Sound:** off until the tray speaker turns it on (localStorage `sound`): a chime per notification, a pop per
  shatter, a click per guided click, a chord at the finale. Web Audio, no files.
- **The dive:** opening a change's details zooms through the Tunero window into the place it writes, the registry
  path drawn as a tunnel of key segments (CSS 3D transforms over the shader), then back out.

### Brand and third parties

No Microsoft logos and no real third-party app icons or names on the demo PC: generic names and icons (Fluent UI
System Icons, MIT) such as "Cloud sync" or "Game launcher". The removable inbox apps are the exception for names: they
show the display names from the catalog (`appx.json`), because that list is what the app offers, with generic icons.
The look is Windows 11; the labels are ours.

## Classic page

`/classic/` (and `/de/classic/`): a calm, scrollable, static page that works without JavaScript. Download and package
manager commands, the trust facts, the rating board (every check and tweak with its gaming rating), FAQ, privacy and
legal links. It is the target of "Exit to classic page", the `<noscript>` content, and the first link in the page for
screen readers and keyboard users ("Skip to classic page").

## Phones and small screens

Phones do not get the demo PC (owner, 2026-10-21): the home page sends them to the classic page before anything
paints, in the same language and with the hash, and the classic page hides its demo link there. A phone is narrower
than 900 px, or a touch screen under 500 px tall (held sideways); the query lives in `src/lib/phone.ts`.

A desktop browser made narrower than 900 px after the page loaded keeps the demo in its compact layout: Tunero fills
the screen, a strip above it counts the demo PC's nags, startup programs and apps, and the other windows open as
sheets.

## Accessibility and motion

- `prefers-reduced-motion`: no boot, a still wallpaper, instant window and rewind transitions (the state still
  changes, step by step, so undo still reads).
- Every window, icon and control is reachable by keyboard; Esc closes the active window; focus moves into a window
  when it opens. Windows are `role="dialog"` with their title as label.
- Text contrast meets WCAG AA in both themes; the shader never sits behind body text without a solid surface.

## Tech

- `website-v2/`, Astro 7 (static output, the same i18n routing as v1: `/` English, `/de/` German) with one Svelte 5
  island for the desktop. Svelte transitions and the Web Animations API for motion; no GSAP and no other animation
  library.
- `src/lib/data.ts`: facts from the repository at build time (version, counts, check titles and ratings, tweak
  ratings, registry values, changelog), ported from v1 so v2 does not depend on the v1 folder.
- `src/lib/demo.ts`: the demo scenario, a list of check and tweak ids, each with the desktop effect it drives. Titles,
  ratings, summaries and values are read from the catalog; the build fails when an id no longer exists.
- `src/i18n/en.ts`, `de.ts`: all site copy.
- Fonts: Segoe UI Variable and Cascadia Mono where Windows has them, `system-ui` and `ui-monospace` elsewhere. No web
  fonts.
- Budget: at most 150 KB of JavaScript (gzip) for the desktop island, first paint of the desktop under 1.5 s on a
  mid range laptop.
- Deploy: none yet. `pages.yml` builds only `website/`; switching tunero.dev to v2 is a separate step the owner
  decides on (then v2 moves into `website/`, or the workflow points at `website-v2/`).

## Testing

- `DocLintTests` already checks the copy rules in `website/src`; it gets `website-v2/src` too.
- The build fails on a missing demo id, a count that disagrees with the README, or a missing translation key.
- Checked in Chrome at 1920 x 1080, 1366 x 768 and 390 x 844, light and dark, English and German, with reduced
  motion, keyboard only, and with WebGL turned off. Lighthouse performance and accessibility on both pages.
