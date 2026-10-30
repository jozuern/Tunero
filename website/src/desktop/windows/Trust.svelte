<script lang="ts">
  // "Why trust it" as a File Explorer folder: one file per reason, read in the preview pane. While the sync provider
  // tweak is not applied, Explorer shows a cloud backup suggestion above the files, as Windows does.
  import { slide } from 'svelte/transition';
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import type { TrustItem } from '../../i18n/en.ts';
  import { useStore } from '../store.svelte.ts';
  import { animated } from '../motion.ts';
  import Icon from '../Icon.svelte';

  let { copy, items, intro }: { copy: DesktopStrings['explorer']; items: TrustItem[]; intro: string } = $props();
  const store = useStore();
  let selected = $state(0);
  const item = $derived(items[selected]);
  const motion = (node: Element) => slide(node, { duration: animated(220) });
</script>

<div class="explorer">
  <nav class="side" aria-label={copy.thisPc}>
    <span><Icon name="home" size={16} />{copy.home}</span>
    <span class="current"><Icon name="folder" size={16} />{copy.folder}</span>
    <span><Icon name="desktop" size={16} />{copy.thisPc}</span>
  </nav>
  <div class="main">
    <p class="crumbs">{copy.thisPc} &gt; {copy.folder}</p>
    {#if store.active('explorer-sync-ad')}
      <div class="ad" data-effect="explorer-sync-ad" transition:motion>
        <Icon name="cloud" size={20} />
        <span><strong>{copy.syncAd.title}</strong> {copy.syncAd.body}</span>
        <button type="button" class="fake-btn" title={copy.syncAd.hint} onclick={() => store.openTweakFor('explorer-sync-ad')}>{copy.syncAd.action}</button>
      </div>
    {/if}
    <div class="split">
      <ul class="files">
        {#each items as file, i (file.title)}
          <li>
            <button type="button" class:current={i === selected} onclick={() => (selected = i)}>
              <Icon name="notepad" size={20} />
              <span>{file.title}</span>
            </button>
          </li>
        {/each}
      </ul>
      <article class="preview" aria-live="polite">
        <h2>{item.title}</h2>
        {#if selected === 0}<p class="intro">{intro}</p>{/if}
        <p>{item.body}</p>
        {#if item.code}<code>{item.code}</code>{/if}
        {#if item.link}
          <a href={item.link.href} target="_blank" rel="noopener">{item.link.label} <Icon name="open" size={16} /></a>
        {/if}
      </article>
    </div>
  </div>
</div>

<style>
  .explorer {
    display: flex;
    flex: 1;
    min-height: 0;
    user-select: text;
  }

  .side {
    display: flex;
    flex-direction: column;
    gap: 2px;
    width: 190px;
    flex: none;
    padding: 8px;
    border-right: 1px solid var(--stroke);
  }

  .side span {
    display: flex;
    align-items: center;
    gap: 12px;
    height: 32px;
    padding: 0 8px;
    border-radius: var(--radius-sm);
    color: var(--ink);
  }

  .side .current {
    background: var(--stroke);
  }

  .main {
    display: flex;
    flex-direction: column;
    flex: 1;
    min-width: 0;
    background: var(--surface);
  }

  .crumbs {
    margin: 0;
    padding: 8px 16px;
    border-bottom: 1px solid var(--stroke);
    color: var(--ink-2);
    font-size: 12px;
  }

  .ad {
    display: flex;
    align-items: center;
    gap: 12px;
    margin: 12px 16px 0;
    padding: 12px 16px;
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--accent) 14%, var(--surface-2));
  }

  .ad span:not(.fake-btn) {
    flex: 1;
  }

  .fake-btn {
    border: none;
    font: inherit;
    padding: 4px 12px;
    border-radius: var(--radius-sm);
    background: var(--accent);
    color: var(--accent-ink);
    white-space: nowrap;
  }

  .split {
    display: grid;
    grid-template-columns: minmax(180px, 280px) 1fr;
    flex: 1;
    min-height: 0;
  }

  .files {
    margin: 0;
    padding: 8px;
    overflow: auto;
    list-style: none;
  }

  .files button {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
    padding: 8px;
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    background: none;
    color: var(--ink);
    font: inherit;
    text-align: left;
  }

  .files button:hover {
    background: var(--stroke);
  }

  .files button.current {
    border-color: var(--stroke-strong);
    background: color-mix(in srgb, var(--accent) 16%, transparent);
  }

  .preview {
    padding: 16px 20px;
    border-left: 1px solid var(--stroke);
    overflow: auto;
  }

  h2 {
    margin: 0 0 8px;
    font-family: var(--font-display);
    font-size: 20px;
    font-weight: 600;
  }

  p {
    margin: 0 0 12px;
    color: var(--ink-2);
    line-height: 1.55;
  }

  .intro {
    color: var(--ink);
  }

  code {
    display: block;
    margin-bottom: 12px;
    padding: 8px 12px;
    border-radius: var(--radius-sm);
    background: var(--surface-2);
    border: 1px solid var(--stroke);
    font-family: var(--mono);
    font-size: 12px;
    overflow-wrap: anywhere;
  }

  a {
    display: inline-flex;
    align-items: center;
    gap: 4px;
  }

  @media (max-width: 899px) {
    .side {
      display: none;
    }

    .split {
      grid-template-columns: 1fr;
      grid-template-rows: auto minmax(0, 1fr);
    }

    .files {
      overflow: visible;
    }

    .preview {
      border-left: none;
      border-top: 1px solid var(--stroke);
    }
  }
</style>
