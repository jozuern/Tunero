<script lang="ts">
  // What a change writes, as the app's detail pane lists it: the value, then from the old data to the new.
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import type { Write } from '../../lib/demo.ts';
  import { fill } from './appKeys.ts';

  let { writes, copy, ondive }: { writes: Write[]; copy: DesktopStrings['app']; ondive?: (write: Write) => void } = $props();
</script>

<ul class="writes">
  {#each writes as write (write.where)}
    <li>
      {#if write.registry}<code>{write.where}</code>{:else}<span class="setting">{write.where}</span>{/if}<span class="change"
        >: {fill(copy.fromTo, write.from, '')}<b class:value={write.registry}>{write.to}</b></span
      >
      {#if ondive && write.registry}
        <button type="button" class="dive" onclick={() => ondive(write)}>{copy.showWhere}</button>
      {/if}
    </li>
  {/each}
</ul>

<style>
  .writes {
    display: grid;
    gap: 6px;
    margin: 8px 0 0;
    padding: 0;
    list-style: none;
    font-size: 13px;
  }

  li {
    overflow-wrap: anywhere;
  }

  code {
    font-family: var(--mono);
    font-size: 12px;
  }

  .change {
    color: var(--ink-2);
  }

  b {
    color: var(--ink);
    font-weight: 600;
  }

  b.value {
    font-family: var(--mono);
    font-size: 12px;
  }

  .dive {
    margin-left: 8px;
    padding: 0;
    border: none;
    background: none;
    color: var(--accent);
    font: inherit;
    font-size: 12px;
    text-decoration: underline;
    text-underline-offset: 2px;
  }
</style>
