<script lang="ts">
  // Notifications of the demo PC. Each comes from something Tunero can turn off (a tweak or a startup program), so
  // it stops the moment that change is applied and comes back with Undo.
  import { onMount } from 'svelte';
  import { fly } from 'svelte/transition';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { useStore } from './store.svelte.ts';
  import { reducedMotion } from './store.svelte.ts';
  import { toastingStartup } from './startup.ts';
  import Icon from './Icon.svelte';
  import type { IconName } from './icons.ts';

  type Key = keyof DesktopStrings['toasts'];

  let { copy, running }: { copy: DesktopStrings; running: boolean } = $props();
  const store = useStore();

  const sources: { key: Key; icon: IconName; live: () => boolean }[] = [
    { key: 'tips', icon: 'lightbulbBig', live: () => store.active('tips-toasts') },
    { key: 'cloud', icon: 'cloud', live: () => !store.applied({ kind: 'startup', id: toastingStartup.cloud }) },
    { key: 'feedback', icon: 'star', live: () => store.active('feedback-toast') },
    { key: 'launcher', icon: 'games', live: () => !store.applied({ kind: 'startup', id: toastingStartup.launcher }) },
    { key: 'updater', icon: 'sync', live: () => !store.applied({ kind: 'startup', id: toastingStartup.updater }) },
  ];

  let shown = $state<Key[]>([]);
  const visible = $derived(shown.filter((key) => sources.find((s) => s.key === key)!.live()));
  const quietUntil = new Map<Key, number>();
  let next = 0;

  function showNext() {
    if (!running || document.hidden) return;
    for (let i = 0; i < sources.length; i++) {
      const source = sources[(next + i) % sources.length];
      if (!source.live() || shown.includes(source.key) || (quietUntil.get(source.key) ?? 0) > Date.now()) continue;
      next = (sources.indexOf(source) + 1) % sources.length;
      shown = [...shown.slice(-1), source.key];
      setTimeout(() => hide(source.key, 12000), 7000);
      return;
    }
  }

  function hide(key: Key, quiet: number) {
    shown = shown.filter((k) => k !== key);
    quietUntil.set(key, Date.now() + quiet);
  }

  onMount(() => {
    const first = setTimeout(showNext, 1200);
    const timer = setInterval(showNext, 4500);
    return () => {
      clearTimeout(first);
      clearInterval(timer);
    };
  });

  const motion = (node: Element) => fly(node, { x: reducedMotion() ? 0 : 360, duration: reducedMotion() ? 0 : 260, opacity: 0 });
</script>

<div class="toasts" aria-live="polite">
  {#each visible as key (key)}
    {@const toast = copy.toasts[key]}
    {@const source = sources.find((s) => s.key === key)!}
    <div class="toast" role="status" in:motion out:motion>
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
    background: var(--acrylic);
    backdrop-filter: blur(30px) saturate(1.6);
    -webkit-backdrop-filter: blur(30px) saturate(1.6);
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
