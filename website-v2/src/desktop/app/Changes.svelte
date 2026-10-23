<script lang="ts">
  // Changes: everything applied, newest first, each with Undo, and Undo all, which rewinds the desktop step by step.
  import { fade } from 'svelte/transition';
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import type { Change } from '../../lib/demo-state.ts';
  import { useStore, reducedMotion } from '../store.svelte.ts';
  import Icon from '../Icon.svelte';
  import { single } from '../clicks.ts';
  import type { IconName } from '../icons.ts';
  import { startupIcons } from '../startup.ts';
  import { fill, type AppStrings } from './appKeys.ts';
  import { handOff } from './focus.ts';

  let { app }: { app: AppStrings; copy: DesktopStrings['app'] } = $props();
  const store = useStore();
  const demo = store.demo;

  const icons: Record<Change['kind'], IconName> = { finding: 'desktop', tweak: 'wrench', app: 'apps', startup: 'desktop' };
  const icon = (change: Change): IconName =>
    change.kind === 'startup' ? startupIcons[demo.startup.findIndex((s) => s.id === change.id)] : icons[change.kind];
  const lookup = (change: Change) => {
    const list = { finding: demo.findings, tweak: demo.tweaks, app: demo.apps, startup: demo.startup }[change.kind];
    return list.find((item) => item.id === change.id)!;
  };
  const entries = $derived([...store.undoable].reverse());

  let undone = $state<number | null>(null);
  /** The page's title: where the keyboard focus goes when the list it was in is empty. */
  let title: HTMLElement;
  let list: HTMLElement | undefined = $state();

  // Not disabled while it plays (a disabled button drops the focus): it only does nothing then.
  async function undoAll() {
    if (entries.length === 0 || store.rewinding) return;
    const count = store.undoable.length;
    await store.undoAll();
    undone = count;
    void handOff(null, () => title);
  }

  /** Undoes one entry; the focus moves to the Undo of the entry that takes its place, or to the title after the last. */
  function undoOne(change: Change, index: number, event: Event) {
    const key = `${change.kind}:${change.id}`;
    void handOff(event.currentTarget, () => {
      const rest = [...(list?.querySelectorAll<HTMLElement>('[data-undo]') ?? [])].filter((b) => b.dataset.undo !== key);
      return rest[Math.min(index, rest.length - 1)] ?? title;
    });
    store.undo(change);
  }

  const motion = (node: Element) => fade(node, { duration: reducedMotion() ? 0 : 160 });
</script>

<header class="head">
  <div>
    <h1 bind:this={title} tabindex="-1">{app.Changes_Title}</h1>
    <p class="sub">{app.Changes_Backups}</p>
  </div>
  <button type="button" class="btn primary" aria-disabled={entries.length === 0 || store.rewinding} onclick={undoAll}>
    <Icon name="undo" size={16} />{app.Changes_UndoAll}
  </button>
</header>

{#if undone !== null && entries.length === 0}
  <div class="result" role="status">{fill(app.Result_UndoAll, undone)}</div>
{/if}

{#if entries.length === 0 && undone === null}
  <p class="empty">{app.Changes_LogEmpty}</p>
{:else}
  <ul bind:this={list}>
    {#each entries as change, index (`${change.kind}:${change.id}`)}
      {@const item = lookup(change)}
      <li class="card" out:motion>
        <Icon name={icon(change)} size={20} />
        <span class="text">
          <strong>{item.title}</strong>
          {#if item.writes.length > 0}<span>{fill(app.Changes_Entries, item.writes.length)}</span>
          {:else if change.kind === 'startup'}<span>{app.Startup_SignInKinds}: {app.Startup_Disabled}</span>{/if}
        </span>
        <button type="button" class="btn" data-undo="{change.kind}:{change.id}" aria-label="{app.Tweak_Undo}: {item.title}" onclick={single((event) => undoOne(change, index, event))}>{app.Tweak_Undo}</button>
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
