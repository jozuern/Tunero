<script lang="ts">
  // Phones: the demo PC's state at a glance above the windows. Notifications, startup programs and preinstalled apps,
  // each counted from the same state the desktop shows, so every fix and undo changes a number here.
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { useStore } from './store.svelte.ts';
  import { toastingStartup } from './startup.ts';
  import Icon from './Icon.svelte';
  import Watermark from './Watermark.svelte';
  import type { IconName } from './icons.ts';

  let { copy, classic, watermark }: { copy: DesktopStrings['phone']; classic: { href: string; label: string }; watermark: string[] } = $props();
  const store = useStore();

  const notifications = $derived(
    [
      store.active('tips-toasts'),
      store.active('feedback-toast'),
      ...Object.values(toastingStartup).map((id) => !store.applied({ kind: 'startup', id })),
    ].filter(Boolean).length,
  );
  const startup = $derived(store.demo.startup.filter((s) => !store.applied({ kind: 'startup', id: s.id })).length);
  const apps = $derived(store.demo.apps.filter((a) => !store.applied({ kind: 'app', id: a.id })).length);

  // Windows and Start sit below the strip, whose height depends on how its text wraps.
  let height = $state(64);
  $effect(() => {
    document.documentElement.style.setProperty('--strip', `${height}px`);
    return () => document.documentElement.style.removeProperty('--strip');
  });
</script>

<div class="strip" bind:offsetHeight={height}>
  {#each [
    { anchor: 'toasts', icon: 'chat', count: notifications, label: copy.notifications, short: copy.short.notifications },
    { anchor: 'tray', icon: 'desktop', count: startup, label: copy.tray, short: copy.short.tray },
    { anchor: 'start', icon: 'apps', count: apps, label: copy.apps, short: copy.short.apps },
  ] as chip (chip.anchor)}
    {@const text = chip.label.replace('{0}', String(chip.count))}
    <span data-anchor={chip.anchor} class:clear={chip.count === 0} title={text} aria-label={text} role="img">
      <Icon name={chip.icon as IconName} size={16} />{chip.short}<b>{chip.count}</b>
    </span>
  {/each}
  <div class="label"><Watermark wrap lines={[`${watermark[0]}. ${watermark[1]}`]} /></div>
  <a href={classic.href}>{classic.label}</a>
</div>

<style>
  .strip {
    position: fixed;
    left: 0;
    right: 0;
    top: 0;
    z-index: 9000;
    display: flex;
    flex-wrap: wrap;
    gap: 6px 8px;
    padding: 8px 12px 6px;
    background: var(--acrylic);
    -webkit-backdrop-filter: blur(30px) saturate(1.6);
    backdrop-filter: blur(30px) saturate(1.6);
    border-bottom: 1px solid var(--stroke-strong);
    color: var(--ink);
    font-size: 12px;
    scrollbar-width: none;
  }

  span {
    display: flex;
    align-items: center;
    gap: 6px;
    flex: none;
    padding: 4px 10px;
    border-radius: 12px;
    background: color-mix(in srgb, #f7b500 22%, transparent);
    transition: background 300ms;
  }

  b {
    font-weight: 600;
  }

  a {
    flex: none;
    align-self: center;
    margin-left: auto;
    padding: 0 4px;
    color: var(--ink);
  }

  .label {
    flex: 1 1 200px;
    min-width: 0;
  }

  span.clear {
    background: color-mix(in srgb, var(--ok) 22%, transparent);
  }
</style>
