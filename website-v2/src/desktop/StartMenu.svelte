<script lang="ts">
  import { fly } from 'svelte/transition';
  import type { WindowId } from '../lib/demo-state.ts';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { useStore, reducedMotion } from './store.svelte.ts';
  import Icon from './Icon.svelte';
  import TuneroMark from './TuneroMark.svelte';
  import type { IconName } from './icons.ts';

  let { copy, links }: { copy: DesktopStrings; links: { download: string; faq: string; privacy: string } } = $props();
  const store = useStore();

  const windows: { id: WindowId; icon: IconName | 'tunero'; label: string }[] = $derived([
    { id: 'tunero', icon: 'tunero', label: copy.icons.tunero },
    { id: 'trust', icon: 'folder', label: copy.icons.explorer },
    { id: 'notepad', icon: 'notepad', label: copy.icons.notepad },
    { id: 'settings', icon: 'settings', label: copy.icons.settings },
  ]);
  const appIcons: IconName[] = ['news', 'weather', 'grid', 'star', 'video', 'globe'];
  const apps = $derived(store.demo.apps.map((app, i) => ({ ...app, icon: appIcons[i % appIcons.length] })));
  const recommendedIcons: IconName[] = ['lightbulbBig', 'star', 'notepad', 'folder'];

  let query = $state('');
  /** Start opens with the cursor in search, as in Windows. */
  const focusNow = (node: HTMLInputElement) => {
    requestAnimationFrame(() => node.focus({ preventScroll: true }));
  };

  type Key = keyof DesktopStrings['search']['items'];
  const targets: Record<Key, { href?: string; window?: WindowId; icon: IconName | 'tunero' }> = $derived({
    install: { href: links.download, icon: 'download' },
    faq: { href: links.faq, icon: 'question' },
    privacy: { href: links.privacy, icon: 'lock' },
    trust: { window: 'trust', icon: 'folder' },
    changelog: { window: 'notepad', icon: 'notepad' },
    settings: { window: 'settings', icon: 'settings' },
  });
  /** Search: a result matches when its title contains the query or one of its words starts with it. */
  const results = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return [];
    return (Object.keys(copy.search.items) as Key[]).filter((key) => {
      const item = copy.search.items[key];
      return item.title.toLowerCase().includes(q) || item.words.split(' ').some((word) => word.startsWith(q));
    });
  });
  let panel: HTMLElement;

  function onWindowClick(event: MouseEvent) {
    if (store.startOpen && panel && !panel.contains(event.target as Node)) store.startOpen = false;
  }

  function onKeydown(event: KeyboardEvent) {
    if (store.startOpen && event.key === 'Escape') store.startOpen = false;
  }

  const motion = (node: Element) => fly(node, { y: reducedMotion() ? 0 : 40, duration: reducedMotion() ? 0 : 200, opacity: 0 });
  const appMotion = (node: Element) => fly(node, { y: reducedMotion() ? 0 : -24, duration: reducedMotion() ? 0 : 260, opacity: 0 });
</script>

<svelte:window onclick={onWindowClick} onkeydown={onKeydown} />

{#if store.startOpen}
  <div class="start" bind:this={panel} role="dialog" aria-label={copy.start} transition:motion>
    <label class="searchbox">
      <Icon name="search" size={16} />
      <input type="search" bind:value={query} placeholder={copy.searchPlaceholder} aria-label={copy.searchPlaceholder} use:focusNow />
      {#if store.active('search-highlights')}
        <span class="trend" transition:appMotion><Icon name="star" size={16} />{copy.searchHighlight}</span>
      {/if}
    </label>

    {#if query.trim()}
      <section class="results" aria-live="polite">
        <h2>{copy.search.results}</h2>
        {#each results as key (key)}
          {@const item = copy.search.items[key]}
          {@const target = targets[key]}
          {#if target.href}
            <a class="result" href={target.href}>
              <Icon name={target.icon as IconName} size={24} /><span><strong>{item.title}</strong>{item.body}</span>
            </a>
          {:else}
            <button type="button" class="result" onclick={() => store.open(target.window!)}>
              <Icon name={target.icon as IconName} size={24} /><span><strong>{item.title}</strong>{item.body}</span>
            </button>
          {/if}
        {:else}
          <p class="none">{copy.search.none}</p>
        {/each}
      </section>
    {:else}
    {#if store.active('search-highlights')}
      <section class="trending" transition:appMotion>
        <h2>{copy.search.trending}</h2>
        <ul>
          {#each copy.search.trendingItems as item (item)}<li><Icon name="star" size={16} />{item}</li>{/each}
        </ul>
      </section>
    {/if}
    <h2>{copy.pinned}</h2>
    <div class="grid">
      {#each windows as item (item.id)}
        <button type="button" class="tile" onclick={() => store.open(item.id)}>
          {#if item.icon === 'tunero'}<TuneroMark size={32} />{:else}<Icon name={item.icon} size={32} />{/if}
          <span>{item.label}</span>
        </button>
      {/each}
      {#each apps as app (app.id)}
        {#if !store.applied({ kind: 'app', id: app.id })}
          <span class="tile static" out:appMotion in:appMotion>
            <Icon name={app.icon} size={32} />
            <span>{app.title}</span>
          </span>
        {/if}
      {/each}
    </div>

    {#if store.active('start-recommended')}
      <section class="recommended" transition:appMotion>
        <h2>{copy.recommended}</h2>
        <ul>
          {#each copy.recommendedItems as item, i (item)}
            <li><Icon name={recommendedIcons[i]} size={24} /><span>{item}</span></li>
          {/each}
        </ul>
      </section>
    {/if}

    {/if}

    <footer>
      <span class="user">
        <span class="avatar"><Icon name="person" size={20} />{#if store.active('start-account')}<span class="badge"></span>{/if}</span>
        {copy.user}
      </span>
      {#if store.active('start-account')}
        <span class="nag" transition:appMotion>{copy.accountNag}</span>
      {/if}
      <span class="power" title={copy.power}><Icon name="power" size={20} /></span>
    </footer>
  </div>
{/if}

<style>
  .start {
    position: fixed;
    left: 50%;
    bottom: 60px;
    z-index: 8500;
    width: min(640px, calc(100vw - 24px));
    max-height: calc(100vh - 84px);
    overflow: auto;
    margin-left: calc(min(640px, calc(100vw - 24px)) / -2);
    padding: 24px 32px 0;
    border: 1px solid var(--stroke-strong);
    border-radius: var(--radius);
    background: var(--acrylic-strong);
    backdrop-filter: blur(40px) saturate(1.6);
    -webkit-backdrop-filter: blur(40px) saturate(1.6);
    box-shadow: var(--shadow);
    color: var(--ink);
  }

  .searchbox {
    display: flex;
    align-items: center;
    gap: 12px;
    height: 36px;
    padding: 0 12px;
    border: 1px solid var(--stroke-strong);
    border-bottom: 2px solid var(--accent);
    border-radius: var(--radius-sm);
    background: var(--surface-2);
    color: var(--ink-2);
  }

  .searchbox input {
    flex: 1;
    min-width: 0;
    border: none;
    background: transparent;
    color: var(--ink);
    font: inherit;
    outline: none;
  }

  .trend {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 2px 8px;
    border-radius: var(--radius-sm);
    background: linear-gradient(135deg, #f7b500, #e8590c);
    color: #fff;
    font-size: 12px;
    white-space: nowrap;
  }

  h2 {
    margin: 24px 0 12px;
    font-size: 14px;
    font-weight: 600;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(88px, 1fr));
    gap: 4px;
  }

  .tile {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 8px;
    padding: 12px 4px 8px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--ink);
    font: inherit;
    font-size: 12px;
    text-align: center;
  }

  .tile span {
    overflow: hidden;
    max-width: 100%;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .tile:not(.static):hover {
    background: var(--stroke);
  }

  .static {
    color: var(--ink-2);
  }

  .result {
    display: flex;
    align-items: center;
    gap: 16px;
    width: 100%;
    padding: 12px;
    border: none;
    border-radius: var(--radius-sm);
    background: none;
    color: var(--ink);
    font: inherit;
    text-align: left;
    text-decoration: none;
  }

  .result:hover {
    background: var(--stroke);
  }

  .result span {
    display: flex;
    flex-direction: column;
    color: var(--ink-2);
    font-size: 12px;
  }

  .result strong {
    color: var(--ink);
    font-size: 14px;
    font-weight: 400;
  }

  .none {
    color: var(--ink-2);
  }

  .trending ul {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .trending li {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 12px;
    border: 1px solid var(--stroke-strong);
    border-radius: var(--radius-sm);
    color: var(--ink);
  }

  ul {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 4px 16px;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  li {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 8px;
    color: var(--ink-2);
  }

  footer {
    display: flex;
    align-items: center;
    gap: 16px;
    margin: 24px -32px 0;
    padding: 12px 32px;
    border-top: 1px solid var(--stroke);
    background: var(--stroke);
  }

  .user {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .avatar {
    position: relative;
    display: grid;
    place-items: center;
    width: 32px;
    height: 32px;
    border-radius: 50%;
    background: var(--stroke-strong);
  }

  .badge {
    position: absolute;
    top: -1px;
    right: -1px;
    width: 10px;
    height: 10px;
    border: 2px solid var(--bg);
    border-radius: 50%;
    background: #e8590c;
  }

  .nag {
    flex: 1;
    font-size: 12px;
    color: var(--ink-2);
  }

  .power {
    margin-left: auto;
    display: grid;
    place-items: center;
    width: 36px;
    height: 36px;
    border-radius: var(--radius-sm);
  }
</style>
