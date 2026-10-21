<script lang="ts">
  // "This PC": a small widget in the corner, like the Game Bar's, with the demo PC's values that the biggest fixes
  // change: the refresh rate (counted up when it is fixed), the processor's turbo and the startup programs.
  import { Tween } from 'svelte/motion';
  import { cubicOut } from 'svelte/easing';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { useStore, reducedMotion } from './store.svelte.ts';
  import Icon from './Icon.svelte';

  let { copy }: { copy: DesktopStrings['widget'] } = $props();
  const store = useStore();

  const refresh = store.demo.findings.find((f) => f.effect === 'refresh-rate')!;
  const hz = (text: string) => Number.parseInt(text, 10);
  const refreshOn = $derived(!store.active('refresh-rate'));
  const turboOn = $derived(!store.active('turbo'));
  const startup = $derived(store.demo.startup.filter((s) => !store.applied({ kind: 'startup', id: s.id })).length);

  const rate = new Tween(hz(refresh.writes[0].from), { duration: 1400, easing: cubicOut });
  $effect(() => {
    const target = hz(refreshOn ? refresh.writes[0].to : refresh.writes[0].from);
    void rate.set(target, { duration: reducedMotion() ? 0 : 1400 });
  });
</script>

<aside class="widget" aria-label={copy.title}>
  <h2>{copy.title}</h2>
  <div class="row" class:good={refreshOn} data-anchor="refresh">
    <Icon name="desktop" size={20} />
    <span class="label">{copy.refresh}</span>
    <strong class="value">{Math.round(rate.current)} Hz</strong>
  </div>
  <div class="row" class:good={turboOn} data-anchor="turbo">
    <Icon name="sparkle" size={20} />
    <span class="label">{copy.turbo}</span>
    <strong class="value small">{turboOn ? copy.turboOn : copy.turboOff}</strong>
  </div>
  <div class="row" class:good={startup === 0}>
    <Icon name="apps" size={20} />
    <span class="label">{copy.startup}</span>
    <strong class="value">{startup}</strong>
  </div>
</aside>

<style>
  .widget {
    position: absolute;
    top: 16px;
    right: 16px;
    z-index: 1;
    width: 280px;
    padding: 12px 16px 8px;
    border: 1px solid var(--stroke-strong);
    border-radius: 12px;
    background: var(--acrylic);
    -webkit-backdrop-filter: blur(30px) saturate(1.6);
    backdrop-filter: blur(30px) saturate(1.6);
    box-shadow: var(--shadow);
    color: var(--ink);
  }

  h2 {
    margin: 0 0 4px;
    color: var(--ink-2);
    font-size: 12px;
    font-weight: 600;
  }

  .row {
    display: grid;
    grid-template-columns: 20px 1fr auto;
    align-items: center;
    gap: 10px;
    padding: 8px 0;
    border-top: 1px solid var(--stroke);
    color: var(--ink-2);
    font-size: 13px;
  }

  .row:first-of-type {
    border-top: none;
  }

  .row :global(.icon) {
    color: #f7b500;
    transition: color 400ms;
  }

  .value {
    color: #f7b500;
    font-family: var(--font-display);
    font-size: 20px;
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    text-align: right;
    transition: color 400ms;
  }

  :global([data-theme='light']) .value,
  :global([data-theme='light']) .row :global(.icon) {
    color: #9d5d00;
  }

  .value.small {
    max-width: 130px;
    font-family: inherit;
    font-size: 12px;
    line-height: 1.3;
  }

  .row.good .value,
  .row.good :global(.icon) {
    color: var(--ok) !important;
  }

  @media (max-width: 899px) {
    .widget {
      display: none;
    }
  }
</style>
