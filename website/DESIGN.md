# tunero.dev design

This site (v2) was built from scratch next to the first one (v1) and replaced it on tunero.dev; v1 is gone from the
repository since 2026-10-30 (it is in the git history). The v1 design notes do not apply here. What still applies, because it is about the product and not
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

0. **Boot.** About 2 s, first visit of a session only (remembered in `sessionStorage` with the demo state), skipped by any key or click and when
   the visitor turned animation effects off. Black screen, the Tunero mark, then a few boot lines that are true and read at build
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
  Paused when the tab is hidden, rendered once and frozen with animation effects off, drawn with at most about 720p of
  pixels (1280 x 720; the browser scales it up, and the soft picture looks the same: at full resolution a 1440p monitor
  asked four times the work and dropped frames on laptop graphics, which the headline's rise showed too) and capped at 1.5
  device pixel ratio, drawn on every refresh up to about 110 Hz and on every second, third or fourth one of faster monitors
  (a whole number of refreshes, so each frame stays on screen equally long: 60 frames a second on 144 Hz stutter), and
  drawn with fewer pixels once frames keep coming slowly.
  Until the shader draws, a still of its first frame stands in (`public/wallpaper/`, neglected and fixed in both themes,
  made by `npm run render:wallpaper` after a change to the shader; the browser tests fail while they are out of date), so
  the hand-over cannot be seen. It also stands in without WebGL2.
- **The arrival:** on an untouched demo PC three notifications pile up at once, then come one at a time. A headline
  sits on the wallpaper like the clock of a lock screen, with "Watch Tunero fix it", "Fix it yourself" and a link to
  the comparison. A "This PC" widget in the corner shows the values the two big fixes change (the refresh rate counts
  up from 60 to 144 Hz, turbo goes from blocked to allowed) and the startup count.
- **Shatter:** what a change removes from the desktop (marked `data-effect` or `data-change`) bursts into specks of its
  own colours that stream into Tunero's Changes entry, with a ripple in the wallpaper where it was
  (`particles.ts`, ripples in the shader). Calm, not fireworks: a few hundred soft round specks at most, each on a fixed
  eased path (drift out, wait, arc into the target), leaving in a stream; the ripple bends the picture like glass with a
  thin rim, slows as it grows, is sized by what burst and is gone in 1.8 s.
- **Watch Tunero fix it** (`guide.ts`, plan in `lib/autoplay.ts`): a cursor clicks the real buttons page by page, the
  clicks a visitor would make (Fix, Turn on per group, each startup switch, Remove per app). The app has no fix-all
  button and the demo does not pretend it has one. Any key or click takes over. About 30 seconds. The cursor moves like
  a hand (timed by distance), rests on a button in its hover state before it presses it, and the page glides to the next
  one; a caption above the take-over hint names the step ("Step 1 of 4: Fix what the scan found"). Each click is seen
  where it lands: a fixed problem flashes green, its mark pops and what it wrote unfolds; a group's switches flip one
  after the other; a switched row lights up; a removed app folds away and unfolds under Removed apps; the Changes count
  pops. These belong to the pages, so a visitor's own clicks get them too.
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

- Motion: the demo animates whatever the system's reduced motion setting says (owner, 2026-10-21: the show is the
  point). Settings > Accessibility > Animation effects turns it off (localStorage `motion`): no boot, a still
  wallpaper, no shatter, the guided fix applies at once, instant window and rewind transitions.
- Every window, icon and control is reachable by keyboard; Esc closes the active window; focus moves into a window
  when it opens. Windows are `role="dialog"` with their title as label.
- Text contrast meets WCAG AA in both themes; the shader never sits behind body text without a solid surface.

## Tech

- `website/`, Astro 7 (static output, the same i18n routing as v1: `/` English, `/de/` German) with one Svelte 5
  island for the desktop. Svelte transitions and the Web Animations API for motion; no GSAP and no other animation
  library.
- `src/lib/repo.ts`, `board.ts`, `release.ts`: facts from the repository at build time (counts, check titles and
  ratings, tweak ratings, registry values, changelog, the app's strings; the latest release, asked once per build),
  ported from v1.
- `src/lib/demo.ts`: the demo scenario, a list of check and tweak ids, each with the desktop effect it drives. Titles,
  ratings, summaries and values are read from the catalog; the build fails when an id no longer exists.
- `src/lib/demo-state.ts` and `src/lib/windows.ts`: what the demo state does and where the windows go, without the DOM
  and tested with node; `src/desktop/store.svelte.ts` holds them in Svelte runes, and `src/desktop/effects.ts` shows
  each change on the desktop (shatter, trail).
- `src/i18n/en.ts`, `de.ts`: all site copy.
- Fonts: Segoe UI Variable and Cascadia Mono where Windows has them, `system-ui` and `ui-monospace` elsewhere. No web
  fonts.
- Budget: at most 150 KB of JavaScript (gzip) for the desktop island, first paint of the desktop under 1.5 s on a
  mid range laptop. Today about 73 KB; the build fails above the budget. `astro.config.mjs` adds `modulepreload` links
  for the island's scripts, so they load with the page instead of after it, and the page asks for the wallpaper's still
  with the page.
- Checks: `npm run build` runs `astro check` (the .astro and .ts files), `svelte-check` with warnings as errors (the
  .svelte files) and the build; `npm test` the node tests in `src/lib`; `npm run test:e2e` the browser tests in `e2e/`
  against the built site (Playwright with Chromium: `npx playwright install chromium` once, or `CHROMIUM_PATH` for a
  Chromium already there). The browser tests hold what broke once and was fixed by hand: windows in reach on a small
  screen, the focus on open, minimize and Esc, the session after a reload and a language switch, the boot once per
  visit, a session from an older deploy, no flash of the untouched PC, the visitor taking over the guided fix, a
  notification that comes back visible, a comparison that changes nothing, and the wallpaper's stills and pixel cap.
  `accessibility.spec.ts` runs axe-core's WCAG 2 A and AA rules (`@axe-core/playwright`) on every kind of page in light
  and dark: the desktop, each window and Tunero page, the compact layout, the classic, legal and 404 pages. axe cannot
  judge the contrast of text over the wallpaper, so that stays a check by eye.
- Deploy: `pages.yml` tests and builds `website/`, runs the browser tests and publishes it to tunero.dev;
  `website-check.yml` runs the same on pull requests.

## Testing

- `DocLintTests` checks the copy rules in `website/src`.
- The build fails on a missing demo id, a count that disagrees with the README, or a missing translation key.
- Checked in Chrome at 1920 x 1080, 1366 x 768 and 390 x 844, light and dark, English and German, with reduced
  motion, keyboard only, and with WebGL turned off. Lighthouse performance and accessibility on both pages.
