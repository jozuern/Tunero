<script lang="ts">
  // Tweaks: the demo's tweaks grouped by category with the app's "Turn on" per group, and a detail pane that shows
  // exactly what the selected one writes.
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import type { DemoItem } from '../../lib/demo.ts';
  import { useStore } from '../store.svelte.ts';
  import Rating from './Rating.svelte';
  import Toggle from './Toggle.svelte';
  import Writes from './Writes.svelte';
  import { fill, type AppStrings } from './appKeys.ts';

  let { app, copy, tweakCount }: { app: AppStrings; copy: DesktopStrings['app']; tweakCount: number } = $props();
  const store = useStore();
  const tweaks = store.demo.tweaks;

  const groups = [...new Set(tweaks.map((t) => t.category ?? ''))].map((category) => ({
    category,
    items: tweaks.filter((t) => t.category === category),
  }));

  let selectedId = $state<string | null>(null);
  const selected = $derived(tweaks.find((t) => t.id === selectedId));
  const on = (item: DemoItem) => store.applied({ kind: 'tweak', id: item.id });
  const basisLabel = (basis: string) => fill(copy.basisLabel, copy.basis[basis as keyof typeof copy.basis] ?? basis);

  function set(item: DemoItem, value: boolean) {
    if (value) store.apply([{ kind: 'tweak', id: item.id }]);
    else store.undo({ kind: 'tweak', id: item.id });
  }
</script>

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
          <button
            type="button"
            class="btn"
            disabled={off.length === 0}
            onclick={() => store.apply(group.items.map((item) => ({ kind: 'tweak' as const, id: item.id })))}
          >
            {fill(app.Tweaks_GroupApply, off.length)}
          </button>
        </header>
        {#each group.items as item (item.id)}
          <div class="card tweak" class:selected={selectedId === item.id}>
            <button type="button" class="pick" onclick={() => (selectedId = item.id)}>
              <strong>{item.title}</strong>
              <span>{item.summary}</span>
            </button>
            <Rating value={item.impact} label={fill(app.Impact_Short, item.impact)} />
            <Toggle checked={on(item)} label={item.title} on={app.Tweak_SwitchOn} off={app.Tweak_SwitchOff} onchange={(value) => set(item, value)} />
          </div>
        {/each}
      </section>
    {/each}
  </div>

  <aside class="pane" aria-live="polite">
    {#if selected}
      <h2>{selected.title}</h2>
      <p class="meta">
        <Rating value={selected.impact} label={fill(app.Impact_Short, selected.impact)} />
        <span>{fill(app.Impact_Short, selected.impact)}, {basisLabel(selected.basis)}</span>
      </p>
      <Toggle checked={on(selected)} label={selected.title} on={app.Tweak_SwitchOn} off={app.Tweak_SwitchOff} onchange={(value) => set(selected!, value)} />
      <p>{selected.summary}</p>
      <h3>{copy.whatChanges}</h3>
      <Writes writes={selected.writes} {copy} ondive={(write) => (store.dive = { title: selected!.title, write })} />
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

  .pane h2 {
    margin: 0 0 8px;
    font-size: 18px;
    font-weight: 600;
  }

  .pane h3 {
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

  .layout:not(.open) .pane {
    display: none;
  }

  @container (max-width: 779px) {
    .pane {
      position: static;
      order: -1;
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
