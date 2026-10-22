<script lang="ts">
  // What the visitor meets on the desktop: a headline on the wallpaper, like the clock on a lock screen, with the two
  // ways in (watch Tunero fix the PC, or fix it yourself). It steps aside while a window is open. When everything is
  // fixed, the end card counts what was done and offers the next steps.
  import { fly, fade, scale } from 'svelte/transition';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { tally } from '../lib/demo-state.ts';
  import { useStore, reducedMotion } from './store.svelte.ts';
  import { playGuide } from './guide.ts';
  import { fill } from './app/appKeys.ts';
  import Icon from './Icon.svelte';

  let { copy, checks, tweaks, download, ready }: { copy: DesktopStrings; checks: number; tweaks: number; download: string; ready: boolean } = $props();
  const store = useStore();

  const windowOpen = $derived(Object.values(store.windows).some((w) => w.open && !w.minimized));
  // Not while Undo all plays: the desktop is still filling up, and Watch would start from the middle of it.
  const intro = $derived(ready && !store.guiding && !store.rewinding && !store.compare && !windowOpen && store.clutter > 0);
  const end = $derived(ready && store.finale && !store.guiding && !store.compare && store.clutter === 0);
  const counts = $derived(tally(store.state, store.demo));

  const rise = (node: Element, { delay = 0 }: { delay?: number }) =>
    fly(node, { y: reducedMotion() ? 0 : 24, duration: reducedMotion() ? 0 : 600, delay: reducedMotion() ? 0 : delay, opacity: 0 });
  const swap = (node: Element) => fly(node, { y: reducedMotion() ? 0 : 6, duration: reducedMotion() ? 0 : 280, opacity: 0 });
  const quick = (node: Element) => fade(node, { duration: reducedMotion() ? 0 : 160 });
  const pop = (node: Element) => scale(node, { start: 0.94, duration: reducedMotion() ? 0 : 420, delay: reducedMotion() ? 0 : 500, opacity: 0 });

  /**
   * The end card is a dialog: it takes the keyboard focus on its first action (Install), Escape closes it, and when it
   * goes the focus returns where it was, unless something new took it (the comparison focuses its own handle).
   */
  function dialog(node: HTMLElement) {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    requestAnimationFrame(() => node.querySelector<HTMLElement>('.cta')?.focus({ preventScroll: true }));
    const onKeydown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.stopPropagation();
      store.finale = false;
    };
    node.addEventListener('keydown', onKeydown);
    return {
      destroy() {
        node.removeEventListener('keydown', onKeydown);
        requestAnimationFrame(() => {
          const lost = !document.activeElement || document.activeElement === document.body || node.contains(document.activeElement);
          if (lost && opener?.isConnected) opener.focus({ preventScroll: true });
        });
      },
    };
  }

  function watch(event: MouseEvent) {
    const from = event.detail === 0 ? { x: innerWidth / 2, y: innerHeight / 2 } : { x: event.clientX, y: event.clientY };
    void playGuide(store, from);
  }
</script>

{#if intro}
  <section class="hero" aria-labelledby="hero-title" out:quick>
    <h2 id="hero-title" in:rise={{ delay: 150 }}>{copy.hero.title}</h2>
    <p class="body" in:rise={{ delay: 300 }}>{copy.hero.body}</p>
    <div class="actions" in:rise={{ delay: 450 }}>
      <button type="button" class="cta" onclick={watch}><Icon name="play" size={20} />{copy.hero.watch}</button>
      <button type="button" class="ghost" onclick={() => store.open('tunero')}>{copy.hero.self}</button>
    </div>
    <p class="facts" in:rise={{ delay: 600 }}>
      {fill(copy.hero.facts, checks, tweaks)}
      <button type="button" class="link" onclick={() => (store.compare = true)}><Icon name="split" size={16} />{copy.end.compare}</button>
    </p>
  </section>
{/if}

{#if store.guiding}
  <div class="guiding" role="status" transition:quick>
    {#if store.guideStep}
      {#key store.guideStep.page}
        <p class="step" in:swap>
          <b>{fill(copy.hero.step, store.guideStep.index, store.guideStep.total)}</b>
          {copy.hero.steps[store.guideStep.page]}
        </p>
      {/key}
    {/if}
    <p class="hint">{copy.hero.stop}</p>
  </div>
{/if}

{#if end}
  <div class="end" role="dialog" aria-labelledby="end-title" use:dialog in:pop out:quick>
    <span class="seal"><Icon name="check" size={32} /></span>
    <h2 id="end-title">{copy.end.title}</h2>
    <ul>
      <li>{fill(copy.end.problems, counts.problems)}</li>
      <li>{fill(copy.end.tweaks, counts.tweaks)}</li>
      <li>{fill(copy.end.startup, counts.startup)}</li>
      <li>{fill(copy.end.apps, counts.apps)}</li>
    </ul>
    <p class="note">{copy.end.note}</p>
    <div class="actions">
      <a class="cta" href={download}><Icon name="download" size={20} />{copy.end.install}</a>
      <button
        type="button"
        class="ghost"
        onclick={() => {
          store.finale = false;
          store.compare = true;
        }}><Icon name="split" size={20} />{copy.end.compare}</button
      >
    </div>
    <div class="minor">
      <button
        type="button"
        onclick={() => {
          store.finale = false;
          void store.undoAll();
        }}><Icon name="undo" size={16} />{copy.end.again}</button
      >
      <button type="button" onclick={() => (store.finale = false)}>{copy.end.close}</button>
    </div>
  </div>
{/if}

<style>
  .hero {
    position: absolute;
    left: clamp(132px, 12vw, 220px);
    top: 46%;
    z-index: 1;
    width: min(680px, calc(100vw - 560px));
    transform: translateY(-50%);
    color: var(--hero-ink);
    text-shadow: var(--hero-shadow);
  }

  h2 {
    margin: 0;
    font-family: var(--font-display);
    font-size: clamp(40px, 4.4vw, 78px);
    font-weight: 600;
    line-height: 1.02;
    letter-spacing: -0.025em;
    text-wrap: balance;
  }

  .body {
    max-width: 34em;
    margin: 20px 0 28px;
    font-size: clamp(15px, 1.15vw, 18px);
    line-height: 1.5;
    opacity: 0.88;
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 12px;
  }

  .cta,
  .ghost {
    display: inline-flex;
    align-items: center;
    gap: 10px;
    height: 48px;
    padding: 0 22px;
    border-radius: 8px;
    font: inherit;
    font-size: 16px;
    font-weight: 600;
    text-decoration: none;
    text-shadow: none;
    cursor: pointer;
  }

  .cta {
    border: 1px solid var(--accent);
    background: var(--accent);
    color: var(--accent-ink);
    box-shadow: 0 8px 28px color-mix(in srgb, var(--accent) 45%, transparent);
  }

  .cta:hover {
    filter: brightness(1.08);
  }

  .ghost {
    border: 1px solid var(--stroke-strong);
    background: var(--acrylic);
    -webkit-backdrop-filter: blur(20px) saturate(1.4);
    backdrop-filter: blur(20px) saturate(1.4);
    color: var(--ink);
  }

  .ghost:hover {
    background: var(--acrylic-strong);
  }

  .facts {
    margin: 24px 0 0;
    font-size: 13px;
    opacity: 0.7;
  }

  .link {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    margin-left: 12px;
    padding: 0;
    border: none;
    background: none;
    color: inherit;
    font: inherit;
    text-decoration: underline;
    text-underline-offset: 3px;
    cursor: pointer;
  }

  .guiding {
    position: fixed;
    bottom: 60px;
    left: 50%;
    z-index: 9650;
    display: grid;
    justify-items: center;
    gap: 2px;
    max-width: calc(100vw - 32px);
    margin: 0;
    padding: 10px 20px;
    text-align: center;
    border: 1px solid var(--stroke-strong);
    border-radius: 18px;
    background: var(--acrylic-strong);
    -webkit-backdrop-filter: blur(30px);
    backdrop-filter: blur(30px);
    color: var(--ink);
    font-size: 13px;
    transform: translateX(-50%);
    box-shadow: var(--shadow);
  }

  .guiding p {
    margin: 0;
  }

  .step {
    font-size: 15px;
  }

  .step b {
    margin-right: 6px;
    color: var(--accent);
    font-weight: 600;
  }

  .hint {
    color: var(--ink-2);
    font-size: 12px;
  }

  .end {
    position: fixed;
    left: 50%;
    top: 46%;
    z-index: 9400;
    width: min(500px, calc(100vw - 32px));
    padding: 32px;
    border: 1px solid var(--stroke-strong);
    border-radius: 12px;
    background: var(--acrylic-strong);
    -webkit-backdrop-filter: blur(40px) saturate(1.6);
    backdrop-filter: blur(40px) saturate(1.6);
    box-shadow: var(--shadow-window);
    color: var(--ink);
    translate: -50% -50%;
  }

  .seal {
    display: grid;
    place-items: center;
    width: 56px;
    height: 56px;
    margin-bottom: 16px;
    border-radius: 50%;
    background: color-mix(in srgb, var(--ok) 18%, transparent);
    color: var(--ok);
  }

  .end h2 {
    font-size: 36px;
  }

  .end ul {
    display: grid;
    gap: 4px;
    margin: 16px 0;
    padding: 0;
    list-style: none;
    font-size: 15px;
  }

  .note {
    margin: 0 0 24px;
    color: var(--ink-2);
    font-size: 13px;
    line-height: 1.5;
  }

  .minor {
    display: flex;
    flex-wrap: wrap;
    gap: 16px;
    margin-top: 16px;
  }

  .minor button {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 0;
    border: none;
    background: none;
    color: var(--ink-2);
    font: inherit;
    font-size: 13px;
    text-decoration: underline;
    text-underline-offset: 3px;
    cursor: pointer;
  }

  .minor button:hover {
    color: var(--ink);
  }

  @media (max-width: 899px) {
    .hero {
      display: none;
    }
  }
</style>
