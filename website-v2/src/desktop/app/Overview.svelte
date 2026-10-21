<script lang="ts">
  // Overview: the scan, its findings and a way into the pages that change something.
  import { onDestroy } from 'svelte';
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import { useStore, reducedMotion } from '../store.svelte.ts';
  import Icon from '../Icon.svelte';
  import Rating from './Rating.svelte';
  import Writes from './Writes.svelte';
  import { single } from '../clicks.ts';
  import { fill, type AppStrings, type Page } from './appKeys.ts';

  let { app, copy, go }: { app: AppStrings; copy: DesktopStrings['app']; go: (page: Page) => void } = $props();
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

  const refresh = demo.findings.find((f) => f.fix === 'apply')!;
  const startupFinding = demo.findings.find((f) => f.fix === 'startup')!;
  const refreshFixed = $derived(store.applied({ kind: 'finding', id: refresh.id }));
  const enabledStartup = $derived(demo.startup.filter((s) => !store.applied({ kind: 'startup', id: s.id })).length);
  const tweaksLeft = $derived(demo.tweaks.filter((t) => !store.applied({ kind: 'tweak', id: t.id })).length);
  const appsLeft = $derived(demo.apps.filter((a) => !store.applied({ kind: 'app', id: a.id })).length);
  const problems = $derived(refreshFixed ? 0 : 1);
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
  <h2 class="verdict">{verdict}</h2>
  <ul class="findings">
    <li class="card finding" class:fixed={refreshFixed}>
      <span class="status" class:ok={refreshFixed}><Icon name={refreshFixed ? 'check' : 'warning'} size={20} /></span>
      <div class="text">
        <strong>{refresh.title}</strong>
        <p>{refresh.summary}</p>
        {#if refreshFixed}<Writes writes={refresh.writes} {copy} />{/if}
      </div>
      <Rating value={refresh.impact} label={fill(app.Impact_Short, refresh.impact)} />
      {#if refreshFixed}
        <button type="button" class="btn" onclick={single(() => store.undo({ kind: 'finding', id: refresh.id }))}>{app.Tweak_Undo}</button>
      {:else}
        <button type="button" class="btn primary" onclick={single(() => store.apply([{ kind: 'finding', id: refresh.id }]))}>{app.Fix_Apply}</button>
      {/if}
    </li>
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
