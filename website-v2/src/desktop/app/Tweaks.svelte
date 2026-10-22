<script lang="ts">
  // Tweaks: the demo's tweaks grouped by category with the app's "Turn on" per group, and a detail pane that shows
  // exactly what the selected one writes.
  import { onDestroy } from 'svelte';
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import type { DemoItem } from '../../lib/demo.ts';
  import { useStore } from '../store.svelte.ts';
  import Rating from './Rating.svelte';
  import Toggle from './Toggle.svelte';
  import Writes from './Writes.svelte';
  import Icon from '../Icon.svelte';
  import { flash } from './flash.ts';
  import { fill, type AppStrings } from './appKeys.ts';

  let { app, copy, tweakCount }: { app: AppStrings; copy: DesktopStrings['app']; tweakCount: number } = $props();
  const store = useStore();
  const tweaks = store.demo.tweaks;

  const groups = [...new Set(tweaks.map((t) => t.category ?? ''))].map((category) => ({
    category,
    items: tweaks.filter((t) => t.category === category),
  }));

  let selectedId = $state<string | null>(store.showTweak);
  $effect(() => {
    if (!store.showTweak) return;
    selectedId = store.showTweak;
    store.showTweak = null;
    // Bring it into view once the page has laid out, scrolling only the page (scrollIntoView would also scroll the
    // window frame, which clips its overflow).
    requestAnimationFrame(() => {
      const row = document.querySelector('.tweak.selected');
      const page = row?.closest('.content');
      if (!row || !page) return;
      page.scrollTop += row.getBoundingClientRect().top - page.getBoundingClientRect().top - page.clientHeight / 3;
    });
  });
  const selected = $derived(tweaks.find((t) => t.id === selectedId));
  const on = (item: DemoItem) => store.applied({ kind: 'tweak', id: item.id });
  const basisLabel = (basis: string) => fill(copy.basisLabel, copy.basis[basis as keyof typeof copy.basis] ?? basis);

  /** Spacing of the switches a group's "Turn on" flips: one after the other, as the app's list fills in. */
  const cascadeStep = 110;
  /** The group "Turn on" was just clicked in, for as long as its switches cascade. */
  let cascading = $state<string | null>(null);
  let cascadeTimer: ReturnType<typeof setTimeout> | undefined;

  function turnOn(group: { category: string; items: DemoItem[] }) {
    clearTimeout(cascadeTimer);
    cascading = group.category;
    cascadeTimer = setTimeout(() => (cascading = null), group.items.length * cascadeStep + 900);
    store.apply(group.items.map((item) => ({ kind: 'tweak' as const, id: item.id })));
  }
  onDestroy(() => clearTimeout(cascadeTimer));

  function set(item: DemoItem, value: boolean) {
    if (value) store.apply([{ kind: 'tweak', id: item.id }]);
    else store.undo({ kind: 'tweak', id: item.id });
  }
</script>

{#snippet close()}
  <button type="button" class="close" aria-label={copy.closeDetails} title={copy.closeDetails} onclick={() => (selectedId = null)}>
    <Icon name="dismiss" size={16} />
  </button>
{/snippet}

{#snippet details(item: DemoItem)}
  <p class="meta">
    {#if item.impact > 0}
      <Rating value={item.impact} label={fill(app.Impact_Short, item.impact)} />
      <span>{fill(app.Impact_Short, item.impact)}, {basisLabel(item.basis)}</span>
    {:else}
      <span>{copy.noFpsEffect}</span>
    {/if}
  </p>
  {#if item.ratingReason}<p class="reason">{item.ratingReason}</p>{/if}
  <h3>{copy.whatChanges}</h3>
  <Writes writes={item.writes} {copy} ondive={(write) => (store.dive = { title: item.title, write })} />
{/snippet}

<div class="wrap">
<div class="layout" class:open={selected}>
  <div class="list">
    <h1>{app.Tweaks_Title}</h1>
    <p class="sub">{fill(copy.tweaksHint, tweaks.length, tweakCount)}</p>
    <p class="note">{copy.impactNote} {copy.pickTweak}</p>

    {#each groups as group (group.category)}
      {@const off = group.items.filter((item) => !on(item))}
      <section class="group">
        <header>
          <div>
            <h2>{group.category}</h2>
            <span>{fill(app.Tweaks_GroupCount, group.items.length, group.items.length - off.length)}</span>
          </div>
          {#if off.length > 0}
            <button
              type="button"
              class="btn"
              data-auto="group:{group.category}"
              title={app.Tweaks_GroupApplyTip}
              onclick={() => turnOn(group)}
            >
              {fill(app.Tweaks_GroupApply, off.length)}
            </button>
          {/if}
        </header>
        {#each group.items as item, index (item.id)}
          {@const delay = cascading === group.category ? index * cascadeStep : 0}
          <div class="card tweak" class:selected={selectedId === item.id} use:flash={{ value: on(item), delay }}>
            <button type="button" class="pick" onclick={() => (selectedId = item.id)}>
              <strong>{item.title}</strong>
              <span>{item.summary}</span>
            </button>
            <Rating value={item.impact} label={fill(app.Impact_Short, item.impact)} />
            <Toggle checked={on(item)} label={item.title} on={app.Tweak_SwitchOn} off={app.Tweak_SwitchOff} {delay} onchange={(value) => set(item, value)} />
          </div>
          {#if selectedId === item.id}
            <div class="inline" aria-live="polite">
              {@render close()}
              {@render details(item)}
            </div>
          {/if}
        {/each}
      </section>
    {/each}
  </div>

  <aside class="pane" aria-live="polite">
    {#if selected}
      <header class="pane-head">
        <h2>{selected.title}</h2>
        {@render close()}
      </header>
      {@render details(selected)}
    {/if}
  </aside>
</div>
</div>

<style>
  .wrap {
    container-type: inline-size;
  }

  .layout {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    gap: 24px;
    align-items: start;
  }

  @container (min-width: 780px) {
    .layout.open {
      grid-template-columns: minmax(0, 1fr) 300px;
    }
  }

  .note {
    margin: -8px 0 16px;
    color: var(--ink-3);
    font-size: 12px;
  }

  .group {
    margin-top: 16px;
  }

  .group header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 8px;
  }

  .group h2 {
    margin: 0;
    font-size: 16px;
    font-weight: 600;
  }

  .group header span {
    color: var(--ink-2);
    font-size: 12px;
  }

  .tweak {
    display: flex;
    align-items: center;
    gap: 16px;
    margin-bottom: 4px;
    padding: 12px 16px !important;
  }

  .tweak.selected {
    border-color: var(--accent) !important;
  }

  .pick {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding: 0;
    border: none;
    background: none;
    color: var(--ink);
    font: inherit;
    text-align: left;
  }

  .pick span {
    color: var(--ink-2);
    font-size: 12px;
  }

  .pane {
    position: sticky;
    top: 0;
    padding: 16px;
    border: 1px solid var(--stroke);
    border-radius: var(--radius);
    background: var(--surface-2);
  }

  .pane-head {
    display: flex;
    align-items: flex-start;
    gap: 8px;
  }

  .pane-head h2 {
    flex: 1;
  }

  .close {
    display: grid;
    place-items: center;
    flex: none;
    width: 28px;
    height: 28px;
    margin: -4px -8px 0 auto;
    border: none;
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--ink-2);
  }

  .close:hover {
    background: var(--stroke);
  }

  /* The details under the selected tweak, when the window is too narrow for the side pane. */
  .inline {
    display: none;
    margin: -4px 0 8px;
    padding: 12px 16px;
    border: 1px solid var(--accent);
    border-top: none;
    border-radius: 0 0 var(--radius) var(--radius);
    background: var(--surface-2);
  }

  .inline .close {
    float: right;
    margin: -4px -8px 0 8px;
  }

  .pane h2 {
    margin: 0 0 8px;
    font-size: 18px;
    font-weight: 600;
  }

  .pane h3,
  .inline h3 {
    margin: 16px 0 0;
    font-size: 14px;
    font-weight: 600;
  }

  .meta {
    display: flex;
    align-items: center;
    gap: 12px;
    margin: 0 0 12px;
    color: var(--ink-2);
    font-size: 12px;
  }

  .reason {
    margin: -4px 0 12px;
    color: var(--ink-2);
    font-size: 12px;
  }

  .layout:not(.open) .pane {
    display: none;
  }

  @container (max-width: 779px) {
    .pane {
      display: none;
    }

    .inline {
      display: block;
    }

    .tweak.selected {
      border-radius: var(--radius) var(--radius) 0 0;
    }
  }

  @media (max-width: 899px) {
    .tweak {
      flex-wrap: wrap;
    }

    .pick {
      flex-basis: 100%;
    }
  }
</style>
