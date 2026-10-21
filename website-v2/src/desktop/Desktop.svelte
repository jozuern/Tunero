<script lang="ts">
  // The demo PC: wallpaper, icons, windows, notifications, Start and the taskbar, all driven by one DesktopStore.
  import { onMount } from 'svelte';
  import type { Demo } from '../lib/demo.ts';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { DesktopStore, provideStore } from './store.svelte.ts';
  import Taskbar from './Taskbar.svelte';
  import StartMenu from './StartMenu.svelte';
  import Toasts from './Toasts.svelte';
  import DesktopIcons from './DesktopIcons.svelte';
  import Watermark from './Watermark.svelte';
  import Window from './Window.svelte';
  import TuneroApp from './app/TuneroApp.svelte';
  import Wallpaper from './wallpaper/Wallpaper.svelte';
  import Dive from './Dive.svelte';
  import type { AppStrings } from './app/appKeys.ts';

  let {
    demo,
    copy,
    lang,
    facts,
    app,
  }: {
    demo: Demo;
    copy: DesktopStrings;
    lang: 'en' | 'de';
    facts: { version: string; tweakCount: number; download: string; classicHref: string; otherHref: string };
    app: AppStrings;
  } = $props();

  const store = provideStore(new DesktopStore(demo));
  let ready = $state(false);
  let theme = $state<'light' | 'dark'>('dark');

  onMount(() => {
    store.restore();
    ready = true;
    // The theme lives on <html> (Base.astro sets it, Settings changes it); the wallpaper follows it.
    const root = document.documentElement;
    const read = () => (theme = root.dataset.theme === 'light' ? 'light' : 'dark');
    read();
    const observer = new MutationObserver(read);
    observer.observe(root, { attributes: true, attributeFilter: ['data-theme'] });
    return () => observer.disconnect();
  });
</script>

<div class="desktop" class:ready>
  <Wallpaper clutter={store.clutter} {theme} />
  <DesktopIcons {copy} />

  {#if store.windows.tunero.open}
    <Window id="tunero" title="Tunero" icon="tunero" labels={copy.window}>
      <TuneroApp {app} copy={copy.app} tweakCount={facts.tweakCount} />
    </Window>
  {/if}
  {#if store.windows.trust.open}
    <Window id="trust" title={copy.icons.trust} icon="folder" labels={copy.window}>
      <div class="placeholder">{copy.icons.trust}</div>
    </Window>
  {/if}
  {#if store.windows.notepad.open}
    <Window id="notepad" title={copy.icons.notepad} icon="notepad" labels={copy.window}>
      <div class="placeholder">{copy.icons.notepad}</div>
    </Window>
  {/if}
  {#if store.windows.settings.open}
    <Window id="settings" title={copy.icons.settings} icon="settings" labels={copy.window}>
      <div class="placeholder">{copy.icons.settings}</div>
    </Window>
  {/if}

  <Watermark lines={copy.watermark} />
  {#if store.dive}
    <Dive write={store.dive.write} title={store.dive.title} copy={copy.dive} onclose={() => (store.dive = null)} />
  {/if}
  <Toasts {copy} running={ready} />
  <StartMenu {copy} />
  <Taskbar {copy} {lang} download={facts.download} version={facts.version} classicHref={facts.classicHref} />
</div>

<style>
  .desktop {
    position: fixed;
    inset: 0;
    overflow: hidden;
    user-select: none;
  }

  .placeholder {
    display: grid;
    place-items: center;
    flex: 1;
    font-size: 28px;
    color: var(--ink-3);
  }
</style>
