# tunero.dev design notes

The owner's one firm rule (2026-10-13): the site must not look AI generated. Everything below is how the current design
meets it; change any of it when a better idea serves that rule, and update this file with it.

What reads as generated, and stays out: a numbered mono label in a margin column over every section, the same section
rhythm repeated down the page, a row of big numbers with small labels, a grid of feature tiles, a spec table in the
hero, a near-black page with one bright accent, gradients, glow, glass, pill badges, emoji, invented numbers.

## Idea

The site looks like Windows and reads like an audit. It sits on the real Windows 11 surfaces, so the app's screenshots
look at home on it, and it shows the work: what the scan checks, how every tweak rates for games, what each change
writes, which popular tweaks are left out. One element is loud, the rating board; everything else is quiet.

## Voice

- Same rules as the app: no en or em dashes, no middle dots, no ellipsis characters (the tests check the copy), no emoji.
  Strong or promotional wording is fine where it is true; claims we cannot back stay out.
- The positioning: built to be the only PC optimizer you need. Say it as the aim ("built to be"), and back it with what
  the app holds.
- Numbers come from the repository at build time (`src/lib/data.ts`): never type a count by hand.
- Admit limits on the page (preview status, SmartScreen warnings, what it leaves out).
- Install commands only for package managers that already offer the app (`packages` in `src/lib/data.ts`).

## Type

- `Segoe UI Variable Display` for headings, `Segoe UI Variable Text` for text, `Cascadia Mono` only for registry paths,
  values and commands. All ship with Windows 11; other systems fall back to `system-ui` and `ui-monospace`. No web fonts.
- The hero claim at up to 92 px, section headings up to 56 px, both 600 with tight tracking. The rating numerals are the
  one light weight (300) on the page, up to 104 px.
- No small labels above headings. A heading carries its section.

## Color

| Token | Light | Dark | Use |
|---|---|---|---|
| `--bg` | #F3F3F3 | #202020 | Windows 11 base surface, same as the screenshots |
| `--surface` | #FBFBFB | #2A2A2A | the rating board band and the explanation sheet |
| `--ink` | #1A1A1A | #FFFFFF | text |
| `--ink-2`, `--ink-3` | #555, #6B6B6B | #C8C8C8, #9D9D9D | secondary text |
| `--accent` | #005FB8 | #60CDFF | links, the button, new values, "on some PCs" |
| `--red` | #C42B1C | #FF99A4 | only what goes away: old values, tweaks left out |

Two colors with a meaning each, both from Windows 11. No gradients.

## Page

The scan comes before the tweaks, and the one-app claim comes right after it: the largest gains come from problems
the scan finds, and replacing a folder of tools is the positioning.

The page has three kinds of section, so it does not repeat one rhythm ten times: the argument (board, scan, one tweak,
areas, left out) under large headings, each laid out its own way; and the reference area at the end under small ones.

1. **Hero:** the claim across the page in two lines (a hyphenated word such as "PC-Optimierer" stays whole), the lead
   (scan first, then "built to replace" the tool kinds) and the download side by side, then the Overview window. The
   window hangs into the rating board below it.
2. **Rating board** (the loud element): checks and tweaks share the 0 to 5 scale, numerals down the side. Row 5 has an
   accent numeral and names the checks rated 5 (`topChecks`, read from the check code), because no tweak reaches 5.
   Rows 4 to 1 name every tweak that can matter for games, once, in the row of its highest rating; "on some PCs,
   otherwise N" when the rating depends on the PC. Row 0 names the popular gaming tweaks rated 0 on disputed evidence,
   then one square per remaining tweak.
3. **Scan:** the text on the left (sticky), every check as one running paragraph on the right, alternating ink.
4. **Areas:** "Built to replace a folder full of tools", heading on top, then the app's pages in the groups and order
   of its navigation rail, in two columns, with the selection mark on Overview.
5. **One tweak, end to end:** Mouse acceleration off, its explanation page from the catalog set as a sheet. The
   registry values sit in it where the app puts "What changes", after the summary, old value struck in red and new
   one in accent; the sources sit in its margin. Under the sheet, the Evidence section of the Ultimate Performance
   page quoted (a popular tweak whose evidence does not hold up; the build fails if it stops being rated disputed),
   then the apply steps as a numbered sequence across the page (a real order), then the Tweaks window with the same
   tweak selected.
6. **Left out:** tweak names struck through in red, the reason and source below.
7. **Reference area:** Privacy, Install (a numbered sequence down a line), Support (Ko-fi as the accent button, GitHub
   Sponsors as a standard one, reached from "Support" in the header) and Questions as rows of a settings page: a
   heading of 24 to 30 px on the left, the content on the right, a heavy rule over the first row and thin ones between.

Sections are separated by space, not rules. Rules appear only inside things you read as a set (board rows, tables,
the left-out list, the reference rows).

## Markup

- Astro's HTML compression drops a line break between text and an element or an expression, and the space with it
  ("in the<a>"). Keep a link, `<code>` or `{value}` on the same line as the text before and after it.

## Shape

- 4 px corners on buttons and code, 8 px on screenshots and the explanation sheet. One shadow: under screenshots.
- Container 1160 px, gutter 24 px (16 px under 600 px). No horizontal scroll at 320 px. Left aligned throughout.

## Images

- Only real screenshots of the app, rendered with `--screenshot` (see CONTRIBUTING), never mockups or illustrations.
- Light screenshots in light mode and dark ones in dark mode (`<picture>` with `prefers-color-scheme`), alt text on each.

## Motion and script

- No animation. Two small scripts only: copying the winget command (shown once winget is live), and showing where
  to read about the SmartScreen warning after a click on Download.
