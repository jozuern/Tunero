<script lang="ts">
  // Overview: the scan, its findings and a way into the pages that change something.
  import { onDestroy } from 'svelte';
  import { fly, scale, slide } from 'svelte/transition';
  import { backOut, cubicOut } from 'svelte/easing';
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import { useStore, reducedMotion } from '../store.svelte.ts';
  import Icon from '../Icon.svelte';
  import Rating from './Rating.svelte';
  import Writes from './Writes.svelte';
  import { flash } from './flash.ts';
  import { single } from '../clicks.ts';
  import { fill, type AppStrings, type Page } from './appKeys.ts';
  import type { DemoFinding } from '../../lib/demo.ts';
  import type { Change } from '../../lib/demo-state.ts';

  let { app, copy, go, download }: { app: AppStrings; copy: DesktopStrings['app']; go: (page: Page) => void; download: string } = $props();
  const store = useStore();
  const demo = store.demo;

  /** The startup check reports from more than 8 programs on (F16.startupPrograms). */
  const startupThreshold = 8;

  let scanning = $state(false);
  let probe = $state(0);
  let timer: ReturnType<typeof setInterval> | undefined;

  function scan() {
    if (reducedMotion()) {
      store.markScanned();
      return;
    }
    scanning = true;
    probe = 0;
    clearInterval(timer);
    timer = setInterval(() => {
      probe += 1;
      if (probe >= demo.checksRun) {
        clearInterval(timer);
        scanning = false;
        store.markScanned();
      }
    }, 2600 / demo.checksRun);
  }
  onDestroy(() => clearInterval(timer));

  /** The problems Fix changes: by itself, or by turning on the tweak that fixes it, as the app does. */
  const fixable = demo.findings.filter((f) => f.fix !== 'startup');
  const startupFinding = demo.findings.find((f) => f.fix === 'startup')!;
  const change = (f: DemoFinding): Change => (f.fix === 'tweak' ? { kind: 'tweak', id: f.tweak! } : { kind: 'finding', id: f.id });
  const fixed = (f: DemoFinding) => store.applied(change(f));
  const enabledStartup = $derived(demo.startup.filter((s) => !store.applied({ kind: 'startup', id: s.id })).length);
  const tweaksLeft = $derived(demo.tweaks.filter((t) => !store.applied({ kind: 'tweak', id: t.id })).length);
  const appsLeft = $derived(demo.apps.filter((a) => !store.applied({ kind: 'app', id: a.id })).length);
  const problems = $derived(fixable.filter((f) => !fixed(f)).length);
  // A fixed problem's mark pops in and what the fix wrote unfolds below it; nothing moves with animation effects off.
  const pop = (node: Element) => scale(node, { start: 0.4, duration: reducedMotion() ? 0 : 420, easing: backOut });
  const unfold = (node: Element) => slide(node, { duration: reducedMotion() ? 0 : 380, easing: cubicOut });
  const rise = (node: Element) => fly(node, { y: reducedMotion() ? 0 : 8, duration: reducedMotion() ? 0 : 320, easing: cubicOut });
  const verdict = $derived(problems === 0 ? app.Dash_VerdictNone : problems === 1 ? app.Dash_VerdictOne : fill(app.Dash_VerdictMany, problems));
</script>

<header class="head">
  <div>
    <h1>{app.Nav_Dashboard}</h1>
    <p class="sub">{copy.subtitle}</p>
  </div>
  {#if store.state.scanned && !scanning}
    <button type="button" class="btn" onclick={scan}><Icon name="sync" size={16} />{app.Scan_Again}</button>
  {/if}
</header>

<div class="infobar"><Icon name="info" size={16} /><span>{app.Intro_Text}</span></div>

{#if scanning}
  <section class="scanning" aria-live="polite">
    <h2>{app.Scan_Running}</h2>
    <p class="probe">{fill(app.Scan_Probe, demo.checkTitles[Math.min(probe, demo.checkTitles.length - 1)])}</p>
    <div class="progress"><span style:width="{(probe / demo.checksRun) * 100}%"></span></div>
  </section>
{:else if !store.state.scanned}
  <section class="start">
    <button type="button" class="btn primary big" onclick={scan}>{copy.scanStart}</button>
    <p>{fill(copy.scanNote, demo.checksRun)}</p>
  </section>
{:else}
  {#key verdict}<h2 class="verdict" in:rise>{verdict}</h2>{/key}
  {#if store.clutter === 0}
    <section class="done" aria-live="polite">
      <Icon name="check" size={20} />
      <div>
        <strong>{copy.done.title}</strong>
        <p>{copy.done.body}</p>
        <div class="actions">
          <a class="btn primary" href={download}><Icon name="download" size={16} />{copy.done.install}</a>
          <button type="button" class="btn" disabled={store.rewinding} onclick={() => store.undoAll()}><Icon name="undo" size={16} />{copy.done.again}</button>
        </div>
      </div>
    </section>
  {/if}
  <ul class="findings">
    {#each fixable as finding (finding.id)}
      {@const done = fixed(finding)}
      <li class="card finding" class:fixed={done} use:flash={{ value: done, tone: 'ok' }}>
        {#key done}
          <span class="status" class:ok={done} in:pop><Icon name={done ? 'check' : 'warning'} size={20} /></span>
        {/key}
        <div class="text">
          <strong>{finding.title}</strong>
          <p>{finding.summary}</p>
          {#if done && finding.writes.length > 0}<div in:unfold><Writes writes={finding.writes} {copy} /></div>{/if}
        </div>
        <Rating value={finding.impact} label={fill(app.Impact_Short, finding.impact)} />
        {#if done}
          <button type="button" class="btn" onclick={single(() => store.undo(change(finding)))}>{app.Tweak_Undo}</button>
        {:else}
          <button type="button" class="btn primary" data-auto="finding:{finding.id}" onclick={single(() => store.apply([change(finding)]))}>{app.Fix_Apply}</button>
        {/if}
      </li>
    {/each}
    {#if enabledStartup > startupThreshold}
      <li class="card finding">
        <span class="status info"><Icon name="info" size={20} /></span>
        <div class="text">
          <strong>{startupFinding.title}</strong>
          <p>{startupFinding.summary}</p>
        </div>
        <Rating value={startupFinding.impact} label={fill(app.Impact_Short, startupFinding.impact)} />
        <button type="button" class="btn" onclick={() => go('startup')}>{app.Nav_Startup}</button>
      </li>
    {/if}
  </ul>

  <h2 class="more">{copy.more}</h2>
  <div class="links">
    <button type="button" class="card link" onclick={() => go('tweaks')}>
      <Icon name="wrench" size={20} /><span><strong>{app.Nav_Tweaks}</strong>{fill(copy.moreTweaks, tweaksLeft)}</span>
    </button>
    <button type="button" class="card link" onclick={() => go('startup')}>
      <Icon name="desktop" size={20} /><span><strong>{app.Nav_Startup}</strong>{fill(copy.moreStartup, enabledStartup)}</span>
    </button>
    <button type="button" class="card link" onclick={() => go('apps')}>
      <Icon name="apps" size={20} /><span><strong>{app.Nav_Debloat}</strong>{fill(copy.moreApps, appsLeft)}</span>
    </button>
  </div>
{/if}

<style>
  .head {
    display: flex;
    justify-content: space-between;
    align-items: flex-start;
    gap: 16px;
  }

  .infobar {
    display: flex;
    gap: 12px;
    align-items: flex-start;
    padding: 12px 16px;
    border: 1px solid var(--stroke);
    border-radius: var(--radius);
    background: var(--surface-2);
    color: var(--ink);
  }

  .infobar :global(.icon) {
    margin-top: 2px;
    color: var(--accent);
  }

  .start {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 12px;
    margin-top: 32px;
  }

  .start p {
    margin: 0;
    color: var(--ink-2);
  }

  .big {
    height: 44px !important;
    padding: 0 24px !important;
    font-size: 16px !important;
    font-weight: 600;
  }

  .scanning {
    margin-top: 32px;
  }

  .scanning h2,
  .verdict {
    margin: 24px 0 8px;
    font-family: var(--font-display);
    font-size: 28px;
    font-weight: 300;
    letter-spacing: -0.01em;
  }

  .probe {
    margin: 0 0 12px;
    color: var(--ink-2);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .progress {
    height: 3px;
    border-radius: 2px;
    background: var(--stroke-strong);
    overflow: hidden;
  }

  .progress span {
    display: block;
    height: 100%;
    background: var(--accent);
  }

  .done {
    display: flex;
    gap: 12px;
    align-items: flex-start;
    margin-top: 16px;
    padding: 16px;
    border: 1px solid color-mix(in srgb, var(--ok) 45%, transparent);
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--ok) 12%, var(--surface-2));
  }

  .done > :global(.icon) {
    flex: none;
    margin-top: 2px;
    color: var(--ok);
  }

  .done p {
    margin: 4px 0 12px;
    color: var(--ink-2);
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .actions a {
    text-decoration: none;
  }

  .findings {
    display: grid;
    gap: 8px;
    margin: 16px 0 0;
    padding: 0;
    list-style: none;
  }

  .finding {
    display: flex;
    align-items: center;
    gap: 16px;
  }

  .status {
    flex: none;
    color: #fce100;
  }

  :global([data-theme='light']) .status {
    color: #9d5d00;
  }

  .status.ok {
    color: var(--ok);
  }

  .status.info {
    color: var(--accent);
  }

  .text {
    flex: 1;
    min-width: 0;
  }

  .text p {
    margin: 4px 0 0;
    color: var(--ink-2);
    font-size: 13px;
  }

  .more {
    margin: 32px 0 8px;
    font-size: 16px;
    font-weight: 600;
  }

  .links {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 8px;
  }

  .link {
    display: flex;
    align-items: flex-start;
    gap: 12px;
    color: var(--ink);
    font: inherit;
    text-align: left;
  }

  .link:hover {
    background: var(--stroke) !important;
  }

  .link span {
    display: flex;
    flex-direction: column;
    gap: 2px;
    color: var(--ink-2);
    font-size: 13px;
  }

  .link strong {
    color: var(--ink);
    font-size: 14px;
  }

  @media (max-width: 899px) {
    .finding {
      flex-wrap: wrap;
    }

    .text {
      flex-basis: calc(100% - 40px);
    }
  }
</style>
