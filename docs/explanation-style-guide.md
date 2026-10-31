# Explanation style guide

Every check, advisor item and tweak has one page per language in
`src/Optimizer.Core/Catalog/Docs/<lang>/<id>.md`. The docs lint (`DocLint`, run by `DocLintTests`) enforces the
structure below; a lint issue fails the tests in every configuration, so `dotnet test` reports what CI reports.

## Structure of a check or advisor item

| English | German | Notes |
|---|---|---|
| `# Title` | `# Titel` | may use `{{param}}` |
| `## Summary` | `## Zusammenfassung` | at most 200 characters per status or variant, shown in the list |
| *(generated)* What we found | *(generated)* Was wir gefunden haben | from the finding's facts, never written by hand |
| `## Why it matters` | `## Warum das wichtig ist` | mechanism, effect in games, conditions |
| `## How we detected it` | `## Wie wir es erkennen` | data source and how certain it is |
| `## How to fix` | `## So behebst du es` | numbered steps, vendor-specific through params |
| `## How to check the fix` | `## So prüfst du die Behebung` | usually: run the scan again |
| `## Sources` | `## Quellen` | numbered URLs; every `[n]` in the text must resolve |

Conditional blocks: `::: status Problem,Ok`, `::: variant smr`, `::: if param`, `::: ifnot param`, closed with `:::`.

## Structure of a tweak

| English | German | Notes |
|---|---|---|
| `# Title` | `# Titel` | |
| `## Summary` | `## Zusammenfassung` | at most 200 characters |
| `## How it works` | `## So funktioniert es` | what Windows normally does and what the setting changes |
| `## Why it can help` | `## Warum es helfen kann` | when that matters, in games or otherwise |
| `## Evidence` | `## Belege` | what the sources show; a disputed effect says so |
| `## Trade-offs & risks` | `## Nachteile & Risiken` | what you give up |
| `## When not to use it` | `## Wann du es nicht nutzen solltest` | |
| *(generated)* What changes, Undo | *(generated)* Was sich ändert, Rückgängig machen | from the catalog entry, never written by hand |
| `## Sources` | `## Quellen` | numbered URLs; every `[n]` in the text must resolve |

A tweak with `"undocumented": true` says on both pages that Microsoft (or the vendor) does not document the value
(CONTRIBUTING, "Undocumented values").

## Writing rules

- Say only what is true. Strong words are fine when the facts carry them; a claim about an effect needs a source, and a disputed effect says so, also in the summary.
- Open every source before citing it, and cite only pages that support the claim.
- Give numbers (%, ms) only with a source, or as plain arithmetic (60 Hz = 16.7 ms).
- Explain the mechanism in three steps: what Windows normally does, what this setting changes, and when that matters in games.
- German: write natural German, using "du", not a word-for-word translation. A native speaker reviews it before a release. The docs lint fails on formal "Sie" inside a sentence and on "Ihnen" and the "Ihr" forms anywhere, also at a sentence start, where "Ihre" reads as "your": name the noun instead ("Die Dienstnamen", not "Ihre Namen").
- Name an app page as the navigation rail does in that language: "the Remove apps page", "die Seite Apps entfernen" (the `Nav_` strings). The docs lint checks every "the X page" and "die Seite X".
- No middle dots, en or em dashes, bullet characters (Markdown lists are fine) or ellipsis characters in user-visible text, and no "(s)" or "(e)" plurals. Menu paths use > (Settings > System).
