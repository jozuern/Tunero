<script lang="ts">
  // Notepad with the newest sections of CHANGELOG.md, as they are written there.
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';

  let { copy, entries, href }: { copy: DesktopStrings['notepad']; entries: { version: string; body: string }[]; href: string } = $props();
  const text = $derived(entries.map((entry) => `## ${entry.version}\n\n${entry.body}`).join('\n\n'));
</script>

<div class="notepad">
  <p class="bar">{copy.note} <a {href} target="_blank" rel="noopener">{copy.full}</a></p>
  <pre lang="en" tabindex="0">{text}</pre>
</div>

<style>
  .notepad {
    display: flex;
    flex-direction: column;
    flex: 1;
    min-height: 0;
    background: var(--surface);
    user-select: text;
  }

  .bar {
    margin: 0;
    padding: 8px 16px;
    border-bottom: 1px solid var(--stroke);
    color: var(--ink-2);
    font-size: 12px;
  }

  pre {
    flex: 1;
    margin: 0;
    padding: 12px 16px;
    overflow: auto;
    font-family: var(--mono);
    font-size: 13px;
    line-height: 1.5;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    color: var(--ink);
    outline: none;
  }
</style>
