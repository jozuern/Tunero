<script lang="ts">
  // Notifications of the demo PC. Each comes from something Tunero can turn off (a tweak or a startup program), so
  // it stops the moment that change is applied and comes back with Undo.
  import { untrack } from 'svelte';
  import { chime } from './sound.ts';
  import { fly } from 'svelte/transition';
  import { flip } from 'svelte/animate';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { useStore } from './store.svelte.ts';
  import { reducedMotion } from './store.svelte.ts';
  import { toastingStartup } from './startup.ts';
  import Icon from './Icon.svelte';
  import type { IconName } from './icons.ts';

  type Key = keyof DesktopStrings['toasts'];

  let { copy, running, still = false }: { copy: DesktopStrings; running: boolean; /** The comparison: the first three that are live, standing still. */ still?: boolean } = $props();
  const store = useStore();

  // mark: what the shatter looks for when the change behind a toast is applied (store.svelte.ts).
  const sources: { key: Key; icon: IconName; live: () => boolean; mark: Record<string, string> }[] = [
    { key: 'tips', icon: 'lightbulbBig', live: () => store.active('tips-toasts'), mark: { 'data-effect': 'tips-toasts' } },
    { key: 'cloud', icon: 'cloud', live: () => !store.applied({ kind: 'startup', id: toastingStartup.cloud }), mark: { 'data-change': `startup:${toastingStartup.cloud}` } },
    { key: 'feedback', icon: 'star', live: () => store.active('feedback-toast'), mark: { 'data-effect': 'feedback-toast' } },
    { key: 'launcher', icon: 'games', live: () => !store.applied({ kind: 'startup', id: toastingStartup.launcher }), mark: { 'data-change': `startup:${toastingStartup.launcher}` } },
    { key: 'updater', icon: 'sync', live: () => !store.applied({ kind: 'startup', id: toastingStartup.updater }), mark: { 'data-change': `startup:${toastingStartup.updater}` } },
  ];

  let shown = $state<Key[]>([]);
  const visible = $derived(still ? sources.filter((s) => s.live()).slice(0, 3).map((s) => s.key) : shown.filter((key) => sources.find((s) => s.key === key)!.live()));
  const quietUntil = new Map<Key, number>();
  let next = 0;

  /** Shows the next live toast; `stack` is how many may stand at once. */
  function showNext(stack = innerWidth >= 1500 && innerHeight >= 960 ? 2 : 1) {
    if (!running || document.hidden) return;
    for (let i = 0; i < sources.length; i++) {
      const source = sources[(next + i) % sources.length];
      if (!source.live() || shown.includes(source.key) || (quietUntil.get(source.key) ?? 0) > Date.now()) continue;
      next = (sources.indexOf(source) + 1) % sources.length;
      // More than one at once only where they stay clear of the windows' buttons.
      shown = [...(stack > 1 ? shown.slice(-(stack - 1)) : []), source.key];
      chime();
      setTimeout(() => hide(source.key, 12000), 7000);
      return;
    }
  }

  /**
   * Hides a toast and keeps it away for `quiet` ms. The auto-hide timer of a toast the visitor already dismissed finds it
   * gone and does nothing, so it cannot cut the longer quiet of the dismissal short.
   */
  function hide(key: Key, quiet: number) {
    if (!shown.includes(key)) return;
    shown = shown.filter((k) => k !== key);
    quietUntil.set(key, Math.max(quietUntil.get(key) ?? 0, Date.now() + quiet));
  }

  // A window opened: the pile from the arrival shrinks to the newest one where it would cover the window's buttons.
  $effect(() => {
    const windowOpen = Object.values(store.windows).some((w) => w.open && !w.minimized);
    if (windowOpen && !(innerWidth >= 1500 && innerHeight >= 960)) untrack(() => shown.length > 1 && (shown = shown.slice(-1)));
  });

  // The arrival: on an untouched demo PC the nags pile up at once, three in a row, then come one at a time.
  $effect(() => {
    if (!running || still) return;
    return untrack(() => {
      const untouched = store.state.applied.length === 0;
      const timers = untouched ? [700, 1150, 1600].map((at) => setTimeout(() => showNext(3), at)) : [setTimeout(() => showNext(), 1200)];
      const interval = setInterval(() => showNext(), 4500);
      return () => {
        timers.forEach(clearTimeout);
        clearInterval(interval);
      };
    });
  });

  const motion = (node: Element) => fly(node, { x: reducedMotion() ? 0 : 360, duration: reducedMotion() ? 0 : 260, opacity: 0 });
</script>

<!-- Not a live region: the demo's nags would be read out every few seconds. -->
<div class="toasts" data-anchor="toasts">
  {#each visible as key (key)}
    {@const toast = copy.toasts[key]}
    {@const source = sources.find((s) => s.key === key)!}
    <div class="toast" {...source.mark} in:motion out:motion animate:flip={{ duration: reducedMotion() ? 0 : 220 }}>
      <div class="head">
        <Icon name={source.icon} size={16} />
        <span>{toast.app}</span>
        <button type="button" aria-label={copy.dismiss} title={copy.dismiss} onclick={() => hide(key, 30000)}>
          <Icon name="dismiss" size={16} />
        </button>
      </div>
      <strong>{toast.title}</strong>
      <p>{toast.body}</p>
    </div>
  {/each}
</div>

<style>
  .toasts {
    position: fixed;
    right: 12px;
    bottom: 60px;
    z-index: 8000;
    display: flex;
    flex-direction: column;
    gap: 8px;
    width: min(364px, calc(100vw - 24px));
    pointer-events: none;
  }

  .toast {
    pointer-events: auto;
    padding: 12px 16px 16px;
    border: 1px solid var(--stroke-strong);
    border-radius: var(--radius);
    background: var(--acrylic-strong);
    -webkit-backdrop-filter: blur(30px) saturate(1.6);
    backdrop-filter: blur(30px) saturate(1.6);
    box-shadow: var(--shadow);
    color: var(--ink);
  }

  .head {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;
    font-size: 12px;
    color: var(--ink-2);
  }

  .head span {
    flex: 1;
  }

  .head button {
    display: grid;
    place-items: center;
    width: 28px;
    height: 28px;
    margin: -6px -8px -6px 0;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--ink-2);
  }

  .head button:hover {
    background: var(--stroke);
  }

  strong {
    display: block;
    font-weight: 600;
  }

  p {
    margin: 4px 0 0;
    color: var(--ink-2);
  }
</style>
