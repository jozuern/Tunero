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

  onMount(() => {
    store.restore();
    ready = true;
  });
</script>

<div class="desktop" class:ready>
  <div class="wallpaper-fallback" aria-hidden="true"></div>
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

  .wallpaper-fallback {
    position: absolute;
    inset: 0;
    z-index: 0;
    background:
      radial-gradient(120% 90% at 20% 110%, #2a6fd6 0%, transparent 55%),
      radial-gradient(90% 80% at 85% 10%, #4b3aa8 0%, transparent 60%),
      #0b1a33;
  }

  :global([data-theme='light']) .wallpaper-fallback {
    background:
      radial-gradient(120% 90% at 20% 110%, #9cc9ff 0%, transparent 55%),
      radial-gradient(90% 80% at 85% 10%, #d6c8ff 0%, transparent 60%),
      #e8f1fb;
  }

  .placeholder {
    display: grid;
    place-items: center;
    flex: 1;
    font-size: 28px;
    color: var(--ink-3);
  }
</style>
