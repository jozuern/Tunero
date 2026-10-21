<script lang="ts">
  import { onMount } from 'svelte';
  import type { WindowId } from '../lib/demo-state.ts';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { scale } from 'svelte/transition';
  import { useStore, reducedMotion } from './store.svelte.ts';
  import Icon from './Icon.svelte';
  import TuneroMark from './TuneroMark.svelte';
  import { startupIcons } from './startup.ts';
  import Watermark from './Watermark.svelte';
  import type { IconName } from './icons.ts';

  let {
    copy,
    lang,
    download,
    version,
    classicHref,
    phone,
  }: { copy: DesktopStrings; lang: string; download: string; version: string; classicHref: string; phone: boolean } = $props();

  // Phones cannot run the app: Install copies the link to open on a PC.
  let copied = $state(false);
  async function copyLink() {
    try {
      await navigator.clipboard.writeText(download);
      copied = true;
      setTimeout(() => (copied = false), 4000);
    } catch {
      location.href = download;
    }
  }

  const store = useStore();
  const pinned: { id: WindowId; icon: IconName | 'tunero'; label: string }[] = $derived([
    { id: 'trust', icon: 'folder', label: copy.icons.explorer },
    { id: 'tunero', icon: 'tunero', label: copy.icons.tunero },
    { id: 'notepad', icon: 'notepad', label: copy.icons.notepad },
    { id: 'settings', icon: 'settings', label: copy.icons.settings },
  ]);

  let now = $state(new Date());
  onMount(() => {
    const timer = setInterval(() => (now = new Date()), 15000);
    return () => clearInterval(timer);
  });
  const locale = $derived(lang === 'de' ? 'de-DE' : 'en-US');
  // As Windows shows it: 4:08 PM in English, 16:08 in German.
  const time = $derived(now.toLocaleTimeString(locale, { hour: lang === 'de' ? '2-digit' : 'numeric', minute: '2-digit' }));
  const date = $derived(now.toLocaleDateString(locale, { day: '2-digit', month: '2-digit', year: 'numeric' }));

  const pop = (node: Element) => scale(node, { start: 0.2, duration: reducedMotion() ? 0 : 280, opacity: 0 });
  const trayItems = $derived(store.demo.startup.filter((item) => !store.applied({ kind: 'startup', id: item.id })));
</script>

<nav class="taskbar" aria-label="Taskbar">
  <div class="left">
    <a class="corner" href={classicHref} title={copy.classicPage}>
      <Icon name="arrowLeft" size={16} />
      <span>{copy.classicPage}</span>
    </a>
    <Watermark lines={copy.watermark} />
  </div>

  <div class="center">
    <button
      type="button"
      class="tb start"
      data-anchor="start"
      class:pressed={store.startOpen}
      aria-label={copy.start}
      aria-expanded={store.startOpen}
      title={copy.start}
      onclick={(event) => {
        event.stopPropagation();
        store.startOpen = !store.startOpen;
      }}
    >
      <Icon name="grid" size={24} />
    </button>
    <button
      type="button"
      class="search"
      data-anchor="search"
      onclick={(event) => {
        event.stopPropagation();
        store.startOpen = true;
      }}
    >
      <Icon name="search" size={16} />
      <span class="search-text">{copy.searchShort}</span>
      {#if store.active('search-highlights')}
        <span class="highlight" title={copy.searchHighlight}><Icon name="star" size={16} /></span>
      {/if}
    </button>
    {#each pinned as app (app.id)}
      {@const frame = store.windows[app.id]}
      <button
        type="button"
        class="tb"
        data-anchor={app.id === 'trust' ? 'explorer' : app.id}
        data-taskbar={app.id}
        class:running={frame.open}
        class:active={frame.open && !frame.minimized && store.focused === app.id}
        aria-label={app.label}
        title={app.label}
        onclick={() => store.toggle(app.id)}
      >
        {#if app.icon === 'tunero'}<TuneroMark size={24} />{:else}<Icon name={app.icon} size={24} />{/if}
      </button>
    {/each}
  </div>

  <div class="right">
    {#if phone}
      <button type="button" class="install" onclick={copyLink}>
        <Icon name={copied ? 'check' : 'copy'} size={16} />
        <span>{copy.phone.install}</span>
      </button>
      {#if copied}<span class="copied" role="status">{copy.phone.copied}</span>{/if}
    {:else}
      <a class="install" href={download} title={copy.installTitle.replace('{0}', version)}>
        <Icon name="download" size={16} />
        <span>{copy.install}</span>
      </a>
    {/if}
    <span class="tray" data-anchor="tray" style:--tray-slots={store.demo.startup.length + 3}>
      <span class="tray-icon" title={copy.trayMore}><Icon name="chevronUp" size={16} /></span>
      {#each trayItems as item (item.id)}
        <span class="tray-icon app" title={item.title} in:pop out:pop>
          <Icon name={startupIcons[store.demo.startup.indexOf(item)]} size={16} />
        </span>
      {/each}
      <span class="tray-icon" title={copy.network}><Icon name="wifi" size={16} /></span>
      <span class="tray-icon" title={copy.volume}><Icon name="speaker" size={16} /></span>
    </span>
    <span class="clock" aria-label={copy.clockLabel}>
      <span>{time}</span>
      <span>{date}</span>
    </span>
  </div>
</nav>

<style>
  .taskbar {
    position: fixed;
    left: 0;
    right: 0;
    bottom: 0;
    height: 48px;
    z-index: 9000;
    display: grid;
    grid-template-columns: 1fr auto 1fr;
    align-items: center;
    padding: 0 8px;
    background: var(--acrylic);
    -webkit-backdrop-filter: blur(30px) saturate(1.6);
    backdrop-filter: blur(30px) saturate(1.6);
    border-top: 1px solid var(--stroke-strong);
    color: var(--ink);
  }

  .left {
    justify-self: start;
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }

  .corner {
    flex: none;
    display: flex;
    align-items: center;
    gap: 8px;
    height: 40px;
    padding: 0 12px;
    border-radius: var(--radius-sm);
    color: var(--ink);
    text-decoration: none;
    font-size: 12px;
  }

  .corner:hover,
  .tb:hover,
  .search:hover,
  .tray-icon:hover,
  .clock:hover {
    background: var(--stroke);
  }

  .center {
    display: flex;
    align-items: center;
    gap: 4px;
  }

  .tb {
    position: relative;
    display: grid;
    place-items: center;
    width: 44px;
    height: 40px;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--ink);
  }

  .tb.pressed,
  .tb.active {
    background: var(--stroke-strong);
  }

  .tb.running::after {
    content: '';
    position: absolute;
    bottom: 2px;
    left: 50%;
    width: 6px;
    height: 3px;
    border-radius: 2px;
    background: var(--ink-3);
    transform: translateX(-50%);
    transition: width 150ms;
  }

  .tb.active::after {
    width: 16px;
    background: var(--accent);
  }

  .search {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 168px;
    height: 32px;
    margin: 0 4px;
    padding: 0 8px 0 12px;
    border: 1px solid var(--stroke-strong);
    border-radius: 16px;
    background: var(--stroke);
    color: var(--ink-2);
    font: inherit;
    font-size: 12px;
    text-align: left;
  }

  .search-text {
    flex: 1;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .highlight {
    display: grid;
    place-items: center;
    width: 22px;
    height: 22px;
    border-radius: 50%;
    background: linear-gradient(135deg, #f7b500, #e8590c);
    color: #fff;
  }

  .right {
    justify-self: end;
    display: flex;
    align-items: center;
    gap: 4px;
  }

  .install {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 32px;
    margin-right: 8px;
    padding: 0 12px;
    border-radius: var(--radius-sm);
    background: var(--accent);
    color: var(--accent-ink);
    font-weight: 600;
    font-size: 12px;
    text-decoration: none;
    white-space: nowrap;
  }

  button.install {
    border: none;
    font-family: inherit;
  }

  .copied {
    position: fixed;
    left: 12px;
    right: 12px;
    bottom: 56px;
    padding: 12px 16px;
    border-radius: var(--radius);
    background: var(--surface-2);
    color: var(--ink);
    box-shadow: var(--shadow);
    font-size: 13px;
  }

  .install:hover {
    filter: brightness(1.08);
  }

  /* Room for every icon it starts with, so Install does not move when startup programs leave the tray. */
  .tray {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    min-width: calc(var(--tray-slots) * 28px);
  }

  .tray-icon {
    display: grid;
    place-items: center;
    width: 28px;
    height: 40px;
    border-radius: var(--radius-sm);
  }

  .clock {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    justify-content: center;
    height: 40px;
    padding: 0 8px;
    border-radius: var(--radius-sm);
    font-size: 12px;
    line-height: 1.3;
  }

  @media (max-width: 899px) {
    .taskbar {
      grid-template-columns: auto 1fr;
      padding: 0 4px;
    }

    .left,
    .search,
    .tray,
    .clock {
      display: none !important;
    }

    .tb {
      width: 40px;
    }

    .install {
      margin-right: 4px;
      padding: 0 10px;
    }
  }

  @media (max-width: 1180px) {
    .search {
      width: 44px;
      padding: 0;
      justify-content: center;
      border: none;
      background: transparent;
    }

    .search-text,
    .corner span {
      display: none;
    }
  }

</style>
