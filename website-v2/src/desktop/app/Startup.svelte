<script lang="ts">
  // Startup: the programs that start at sign-in, each with the app's Enabled or Disabled switch.
  import { useStore } from '../store.svelte.ts';
  import Icon from '../Icon.svelte';
  import Toggle from './Toggle.svelte';
  import { startupIcons } from '../startup.ts';
  import type { AppStrings } from './appKeys.ts';

  let { app }: { app: AppStrings } = $props();
  const store = useStore();
</script>

<h1>{app.Startup_Title}</h1>
<p class="sub">{app.Startup_SignInKinds}</p>

<ul>
  {#each store.demo.startup as item, i (item.id)}
    {@const off = store.applied({ kind: 'startup', id: item.id })}
    <li class="card">
      <Icon name={startupIcons[i]} size={20} />
      <strong>{item.title}</strong>
      <Toggle
        checked={!off}
        label={item.title}
        on={app.Startup_Enabled}
        off={app.Startup_Disabled}
        onchange={(value) => (value ? store.undo({ kind: 'startup', id: item.id }) : store.apply([{ kind: 'startup', id: item.id }]))}
      />
    </li>
  {/each}
</ul>

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
</style>
