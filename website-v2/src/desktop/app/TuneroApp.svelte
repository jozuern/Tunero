<script lang="ts">
  // The Tunero window: a recreation of the app's navigation and the pages the demo needs, in the app's words.
  import { scale } from 'svelte/transition';
  import { backOut } from 'svelte/easing';
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import { useStore, reducedMotion } from '../store.svelte.ts';
  import Icon from '../Icon.svelte';
  import type { IconName } from '../icons.ts';
  import type { AppStrings, Page } from './appKeys.ts';
  import Overview from './Overview.svelte';
  import Tweaks from './Tweaks.svelte';
  import Startup from './Startup.svelte';
  import Apps from './Apps.svelte';
  import Changes from './Changes.svelte';

  let { app, copy, tweakCount, download }: { app: AppStrings; copy: DesktopStrings['app']; tweakCount: number; download: string } = $props();
  const store = useStore();

  // The page lives in the store, so the guided fix can turn it.
  const page = $derived(store.appPage);
  let content: HTMLElement;
  $effect(() => {
    if (store.showTweak) go('tweaks');
  });

  // The Changes count pops when it changes, so each change is seen arriving there.
  const bump = (node: Element) => scale(node, { start: 0.5, duration: reducedMotion() ? 0 : 360, easing: backOut });

  function go(next: Page) {
    store.appPage = next;
    content?.scrollTo({ top: 0 });
  }

  const nav: { group?: 'optimize' | 'clean'; page: Page; icon: IconName; label: keyof AppStrings }[] = [
    { page: 'overview', icon: 'home', label: 'Nav_Dashboard' },
    { group: 'optimize', page: 'tweaks', icon: 'wrench', label: 'Nav_Tweaks' },
    { page: 'startup', icon: 'desktop', label: 'Nav_Startup' },
    { group: 'clean', page: 'apps', icon: 'apps', label: 'Nav_Debloat' },
  ];
</script>

<div class="app">
  <nav class="rail" aria-label="Tunero">
    {#each nav as item (item.page)}
      {#if item.group}
        <span class="group">{app[item.group === 'optimize' ? 'Nav_GroupOptimize' : 'Nav_GroupCleanUp']}</span>
      {/if}
      <button type="button" data-nav={item.page} class:current={page === item.page} aria-current={page === item.page ? 'page' : undefined} onclick={() => go(item.page)}>
        <Icon name={item.icon} size={16} />
        <span>{app[item.label]}</span>
      </button>
    {/each}
    <span class="spacer"></span>
    <button type="button" data-changes data-nav="changes" class:current={page === 'changes'} aria-current={page === 'changes' ? 'page' : undefined} onclick={() => go('changes')}>
      <Icon name="history" size={16} />
      <span>{app.Nav_Changes}</span>
      {#if store.undoable.length > 0}{#key store.undoable.length}<span class="count" in:bump>{store.undoable.length}</span>{/key}{/if}
    </button>
  </nav>

  <div class="content" bind:this={content}>
    {#if page === 'overview'}
      <Overview {app} {copy} {go} {download} />
    {:else if page === 'tweaks'}
      <Tweaks {app} {copy} {tweakCount} />
    {:else if page === 'startup'}
      <Startup {app} />
    {:else if page === 'apps'}
      <Apps {app} {copy} />
    {:else}
      <Changes {app} {copy} />
    {/if}
  </div>
</div>

<style>
  .app {
    display: flex;
    flex: 1;
    min-height: 0;
    user-select: text;
  }

  .rail {
    display: flex;
    flex-direction: column;
    gap: 2px;
    width: 220px;
    flex: none;
    padding: 4px 4px 12px;
  }

  .group {
    margin: 16px 8px 4px;
    font-size: 12px;
    font-weight: 600;
    color: var(--ink-2);
  }

  .rail button {
    position: relative;
    display: flex;
    align-items: center;
    gap: 16px;
    height: 36px;
    padding: 0 12px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--ink);
    font: inherit;
    text-align: left;
  }

  .rail button:hover {
    background: var(--stroke);
  }

  .rail button.current {
    background: var(--stroke);
  }

  .rail button.current::before {
    content: '';
    position: absolute;
    left: 0;
    top: 10px;
    bottom: 10px;
    width: 3px;
    border-radius: 2px;
    background: var(--accent);
  }

  .spacer {
    flex: 1;
  }

  .count {
    margin-left: auto;
    min-width: 20px;
    padding: 0 6px;
    border-radius: 10px;
    background: var(--accent);
    color: var(--accent-ink);
    font-size: 12px;
    text-align: center;
  }

  .content {
    flex: 1;
    min-width: 0;
    overflow: auto;
    padding: 24px 32px 32px;
    border-top: 1px solid var(--stroke);
    border-left: 1px solid var(--stroke);
    border-top-left-radius: var(--radius);
    background: var(--surface);
  }

  .content :global(h1) {
    margin: 0 0 4px;
    font-family: var(--font-display);
    font-size: 28px;
    font-weight: 600;
    letter-spacing: -0.01em;
  }

  .content :global(.sub) {
    margin: 0 0 16px;
    color: var(--ink-2);
  }

  .content :global(.card) {
    padding: 16px 20px;
    border: 1px solid var(--stroke);
    border-radius: var(--radius);
    background: var(--surface-2);
  }

  .content :global(.btn) {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    height: 32px;
    padding: 0 12px;
    border: 1px solid var(--stroke-strong);
    border-radius: var(--radius-sm);
    background: var(--surface-2);
    color: var(--ink);
    font: inherit;
    white-space: nowrap;
  }

  .content :global(.btn:hover) {
    background: var(--stroke);
  }

  .content :global(.btn.primary) {
    border-color: var(--accent);
    background: var(--accent);
    color: var(--accent-ink);
  }

  .content :global(.btn.primary:hover) {
    filter: brightness(1.08);
  }

  /* A heading the focus is handed to (app/focus.ts) is a place, not a control: no ring. */
  .content :global([tabindex='-1']:focus) {
    outline: none;
  }

  .content :global(.btn[aria-disabled='true']),
  .content :global(.btn:disabled) {
    opacity: 0.5;
  }

  @media (max-width: 899px) {
    .rail {
      width: 52px;
    }

    .rail span:not(.spacer):not(.count),
    .group {
      display: none;
    }

    .rail .count {
      position: absolute;
      top: 2px;
      right: 2px;
      min-width: 16px;
      padding: 0 4px;
      font-size: 10px;
    }

    .content {
      padding: 16px;
    }
  }
</style>
