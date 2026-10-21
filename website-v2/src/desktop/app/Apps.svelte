<script lang="ts">
  // Remove apps: preinstalled apps from the app's reviewed list, each with Remove; removed ones are listed below.
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import { useStore } from '../store.svelte.ts';
  import Icon from '../Icon.svelte';
  import { fill, type AppStrings } from './appKeys.ts';

  let { app, copy }: { app: AppStrings; copy: DesktopStrings['app'] } = $props();
  const store = useStore();
  const installed = $derived(store.demo.apps.filter((a) => !store.applied({ kind: 'app', id: a.id })));
  const removed = $derived(store.demo.apps.filter((a) => store.applied({ kind: 'app', id: a.id })));
</script>

<h1>{app.Debloat_Title}</h1>
<p class="sub">{app.Debloat_TabStore}</p>

<ul>
  {#each installed as item (item.id)}
    <li class="card">
      <Icon name="apps" size={20} />
      <strong>{item.title}</strong>
      <button type="button" class="btn" aria-label={fill(app.Debloat_RemoveName, item.title)} onclick={() => store.apply([{ kind: 'app', id: item.id }])}>
        {app.Debloat_Remove}
      </button>
    </li>
  {/each}
</ul>

<h2>{app.Debloat_RemovedTitle}</h2>
{#if removed.length === 0}
  <p class="empty">{app.Debloat_RemovedEmpty}</p>
{:else}
  <ul>
    {#each removed as item (item.id)}
      <li class="card done">
        <Icon name="check" size={20} />
        <strong>{item.title}</strong>
        <span>{copy.removed}</span>
      </li>
    {/each}
  </ul>
{/if}

<style>
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

  strong {
    flex: 1;
    font-weight: 400;
  }

  .done {
    color: var(--ink-2);
  }

  .done :global(.icon) {
    color: var(--ok);
  }

  h2 {
    margin: 24px 0 8px;
    font-size: 16px;
    font-weight: 600;
  }

  .empty {
    margin: 0;
    color: var(--ink-2);
  }
</style>
