<script lang="ts">
  // Remove apps: preinstalled apps from the app's reviewed list, each with Remove; removed ones are listed below.
  import { tick } from 'svelte';
  import { fade, slide } from 'svelte/transition';
  import { cubicOut } from 'svelte/easing';
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import { useStore } from '../store.svelte.ts';
  import { animated } from '../motion.ts';
  import Icon from '../Icon.svelte';
  import { appIcons } from '../startup.ts';
  import { single } from '../clicks.ts';
  import { fill, type AppStrings } from './appKeys.ts';
  import { handOff } from './focus.ts';

  let { app, copy }: { app: AppStrings; copy: DesktopStrings['app'] } = $props();
  const store = useStore();
  const installed = $derived(store.demo.apps.filter((a) => !store.applied({ kind: 'app', id: a.id })));
  const removed = $derived(store.demo.apps.filter((a) => store.applied({ kind: 'app', id: a.id })));

  // A removed app folds out of the list, the rows below glide up as it closes, and it unfolds in Removed apps (and the
  // other way on undo); at once with animation effects off. No animate:flip: it takes a leaving row out of the layout,
  // so the heading between the lists would jump under it.
  const leave = (node: Element) => slide(node, { duration: animated(320), easing: cubicOut });
  const arrive = (node: Element) => slide(node, { duration: animated(360), delay: animated(120), easing: cubicOut });

  let removedHeading: HTMLElement;

  /** Removes an app; the keyboard focus moves on to the next app's Remove (or the Removed apps heading after the last). */
  async function remove(id: string, index: number) {
    store.apply([{ kind: 'app', id }]);
    await tick();
    const buttons = [...removedHeading.parentElement!.querySelectorAll<HTMLElement>('[data-auto^="app:"]')].filter((b) => b.dataset.auto !== `app:${id}`);
    (buttons[Math.min(index, buttons.length - 1)] ?? removedHeading).focus({ preventScroll: true });
  }
</script>

<h1>{app.Debloat_Title}</h1>
<p class="sub">{app.Debloat_TabStore}</p>

<ul>
  {#each installed as item, index (item.id)}
    <li class="card" in:arrive out:leave>
      <Icon name={appIcons[store.demo.apps.indexOf(item)]} size={20} />
      <strong>{item.title}</strong>
      <button type="button" class="btn" data-auto="app:{item.id}" aria-label={fill(app.Debloat_RemoveName, item.title)} onclick={single(() => void remove(item.id, index))}>
        {app.Debloat_Remove}
      </button>
    </li>
  {/each}
</ul>

<h2 bind:this={removedHeading} tabindex="-1">{app.Debloat_RemovedTitle}</h2>
{#if removed.length === 0}
  <p class="empty" in:fade={{ duration: animated(200) }}>{app.Debloat_RemovedEmpty}</p>
{/if}
<!-- Always there, so the first app removed slides in like the others. -->
<ul>
  {#each removed as item (item.id)}
    <li class="card done" in:arrive out:leave>
      <Icon name="check" size={20} />
      <strong>{item.title}</strong>
      <span>{copy.removed}</span>
      <button
        type="button"
        class="btn"
        onclick={single((event) => {
          // The row leaves this list: the focus follows the app to its Remove button in the list above.
          void handOff(event.currentTarget, () => removedHeading.parentElement?.querySelector<HTMLElement>(`[data-auto="app:${item.id}"]`));
          store.undo({ kind: 'app', id: item.id });
        })}>{app.Debloat_Reinstall}</button
      >
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
