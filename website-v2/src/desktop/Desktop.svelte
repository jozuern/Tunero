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
  import Boot from './Boot.svelte';
  import PhoneStrip from './PhoneStrip.svelte';
  import { reducedMotion } from './motion.ts';
  import Trust from './windows/Trust.svelte';
  import Notepad from './windows/Notepad.svelte';
  import Settings from './windows/Settings.svelte';
  import type { TrustItem } from '../i18n/en.ts';
  import type { AppStrings } from './app/appKeys.ts';

  let {
    demo,
    copy,
    lang,
    facts,
    app,
    trust,
    changelog,
    links,
  }: {
    demo: Demo;
    copy: DesktopStrings;
    lang: 'en' | 'de';
    facts: { version: string; checkCount: number; tweakCount: number; download: string; classicHref: string; otherHref: string };
    app: AppStrings;
    trust: { intro: string; items: TrustItem[] };
    changelog: { entries: { version: string; body: string }[]; href: string };
    links: { download: string; faq: string; privacy: string };
  } = $props();

  const store = provideStore(new DesktopStore(demo));
  let ready = $state(false);
  let theme = $state<'light' | 'dark'>('dark');
  let phone = $state(false);
  let booting = $state(false);

  function endBoot() {
    booting = false;
    store.markBootSeen();
    delete document.documentElement.dataset.boot;
  }

  onMount(() => {
    store.restore();
    booting = !store.state.bootSeen && !reducedMotion();
    if (!booting) delete document.documentElement.dataset.boot;
    ready = true;
    const narrow = matchMedia('(max-width: 899px)');
    const onWidth = () => (phone = narrow.matches);
    onWidth();
    narrow.addEventListener('change', onWidth);
    // The theme lives on <html> (Base.astro sets it, Settings changes it); the wallpaper follows it.
    const root = document.documentElement;
    const read = () => (theme = root.dataset.theme === 'light' ? 'light' : 'dark');
    read();
    const observer = new MutationObserver(read);
    observer.observe(root, { attributes: true, attributeFilter: ['data-theme'] });
    return () => {
      observer.disconnect();
      narrow.removeEventListener('change', onWidth);
    };
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
      <Trust copy={copy.explorer} items={trust.items} intro={trust.intro} />
    </Window>
  {/if}
  {#if store.windows.notepad.open}
    <Window id="notepad" title={copy.icons.notepad} icon="notepad" labels={copy.window}>
      <Notepad copy={copy.notepad} entries={changelog.entries} href={changelog.href} />
    </Window>
  {/if}
  {#if store.windows.settings.open}
    <Window id="settings" title={copy.icons.settings} icon="settings" labels={copy.window}>
      <Settings copy={copy.settings} {lang} otherHref={facts.otherHref} />
    </Window>
  {/if}

  <Watermark lines={copy.watermark} />
  {#if store.dive}
    <Dive write={store.dive.write} title={store.dive.title} copy={copy.dive} onclose={() => (store.dive = null)} />
  {/if}
  {#if phone}
    <PhoneStrip copy={copy.phone} classic={{ href: facts.classicHref, label: copy.classicPage }} />
  {:else}
    <Toasts {copy} running={ready && !booting} />
  {/if}
  <StartMenu {copy} {links} />
  <Taskbar {copy} {lang} download={facts.download} version={facts.version} classicHref={facts.classicHref} {phone} />
  {#if booting}
    <Boot copy={copy.boot} version={facts.version} checks={facts.checkCount} tweaks={facts.tweakCount} ondone={endBoot} />
  {/if}
</div>

<style>
  .desktop {
    position: fixed;
    inset: 0;
    overflow: hidden;
    user-select: none;
  }

</style>
