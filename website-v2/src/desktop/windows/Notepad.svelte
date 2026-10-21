<script lang="ts">
  // Notepad with the newest sections of CHANGELOG.md, formatted the way Windows 11 Notepad shows Markdown.
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import { blocks, type Span } from './notes.ts';

  let { copy, entries, href }: { copy: DesktopStrings['notepad']; entries: { version: string; body: string }[]; href: string } = $props();
  const parsed = $derived(blocks(entries.map((entry) => `## ${entry.version}\n\n${entry.body}`).join('\n\n')));
</script>

{#snippet line(spans: Span[])}
  {#each spans as span, i (i)}{#if span.bold}<strong>{span.text}</strong>{:else if span.code}<code>{span.text}</code>{:else}{span.text}{/if}{/each}
{/snippet}

<div class="notepad">
  <p class="bar">{copy.note} <a {href} target="_blank" rel="noopener">{copy.full}</a></p>
  <div class="page" lang="en" tabindex="0">
    {#each parsed as block, i (i)}
      {#if block.kind === 'h2'}<h2>{@render line(block.spans)}</h2>
      {:else if block.kind === 'h3'}<h3>{@render line(block.spans)}</h3>
      {:else if block.kind === 'li'}<p class="li">{@render line(block.spans)}</p>
      {:else}<p>{@render line(block.spans)}</p>{/if}
    {/each}
  </div>
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

  .page {
    flex: 1;
    padding: 8px 20px 20px;
    overflow: auto;
    color: var(--ink);
    font-size: 14px;
    line-height: 1.55;
    overflow-wrap: anywhere;
    outline: none;
  }

  .page:focus-visible {
    outline: 2px solid var(--ink);
    outline-offset: -4px;
  }

  h2 {
    margin: 20px 0 8px;
    font-size: 22px;
    font-weight: 600;
  }

  h2:first-child {
    margin-top: 8px;
  }

  h3 {
    margin: 16px 0 6px;
    font-size: 16px;
    font-weight: 600;
  }

  p {
    margin: 0 0 8px;
  }

  .li {
    position: relative;
    padding-left: 20px;
  }

  /* The list marker is drawn, not typed, so the text itself has no bullet character. */
  .li::before {
    content: '';
    position: absolute;
    left: 6px;
    top: 0.7em;
    width: 5px;
    height: 5px;
    border-radius: 50%;
    background: var(--ink-2);
  }

  code {
    padding: 1px 4px;
    border-radius: 4px;
    background: var(--stroke);
    font-family: var(--mono);
    font-size: 12px;
  }
</style>
