<script lang="ts">
  // Changes: everything applied, newest first, each with Undo, and Undo all, which rewinds the desktop step by step.
  import { fade } from 'svelte/transition';
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import type { Change } from '../../lib/demo-state.ts';
  import { useStore, reducedMotion } from '../store.svelte.ts';
  import Icon from '../Icon.svelte';
  import { single } from '../clicks.ts';
  import type { IconName } from '../icons.ts';
  import { fill, type AppStrings } from './appKeys.ts';

  let { app }: { app: AppStrings; copy: DesktopStrings['app'] } = $props();
  const store = useStore();
  const demo = store.demo;

  const icons: Record<Change['kind'], IconName> = { finding: 'desktop', tweak: 'wrench', app: 'apps', startup: 'desktop' };
  const lookup = (change: Change) => {
    const list = { finding: demo.findings, tweak: demo.tweaks, app: demo.apps, startup: demo.startup }[change.kind];
    return list.find((item) => item.id === change.id)!;
  };
  const entries = $derived([...store.undoable].reverse());

  let undone = $state<number | null>(null);

  async function undoAll() {
    const count = store.undoable.length;
    await store.undoAll();
    undone = count;
  }

  const motion = (node: Element) => fade(node, { duration: reducedMotion() ? 0 : 160 });
</script>

<header class="head">
  <div>
    <h1>{app.Changes_Title}</h1>
    <p class="sub">{app.Changes_Backups}</p>
  </div>
  <button type="button" class="btn primary" disabled={entries.length === 0 || store.rewinding} onclick={undoAll}>
    <Icon name="undo" size={16} />{app.Changes_UndoAll}
  </button>
</header>

{#if undone !== null && entries.length === 0}
  <div class="result" role="status">{fill(app.Result_UndoAll, undone)}</div>
{/if}

{#if entries.length === 0}
  <p class="empty">{app.Changes_LogEmpty}</p>
{:else}
  <ul>
    {#each entries as change (`${change.kind}:${change.id}`)}
      {@const item = lookup(change)}
      <li class="card" out:motion>
        <Icon name={icons[change.kind]} size={20} />
        <span class="text">
          <strong>{item.title}</strong>
          {#if item.writes.length > 0}<span>{fill(app.Changes_Entries, item.writes.length)}</span>{/if}
        </span>
        <button type="button" class="btn" onclick={single(() => store.undo(change))}>{app.Tweak_Undo}</button>
      </li>
    {/each}
  </ul>
{/if}

<style>
  .head {
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: flex-start;
    gap: 8px 16px;
  }

  ul {
    display: grid;
    gap: 4px;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  li {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 10px 16px !important;
  }

  .text {
    flex: 1;
    display: flex;
    flex-direction: column;
  }

  .text span {
    color: var(--ink-2);
    font-size: 12px;
  }

  .empty {
    color: var(--ink-2);
  }

  .result {
    margin-bottom: 12px;
    padding: 12px 16px;
    border-radius: var(--radius);
    background: color-mix(in srgb, var(--ok) 16%, transparent);
  }
</style>
