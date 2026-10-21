<script lang="ts">
  // One side of the before/after comparison: the taskbar, Start and the notifications of the neglected or the fixed
  // demo PC, from a view of the store that only shows (store.svelte.ts, comparisonView).
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { comparisonView, provideStore, useStore } from './store.svelte.ts';
  import Taskbar from './Taskbar.svelte';
  import StartMenu from './StartMenu.svelte';
  import Toasts from './Toasts.svelte';

  let {
    side,
    copy,
    lang,
    download,
    version,
    classicHref,
    links,
  }: {
    side: 'before' | 'after';
    copy: DesktopStrings;
    lang: string;
    download: string;
    version: string;
    classicHref: string;
    links: { download: string; faq: string; privacy: string };
  } = $props();

  // svelte-ignore state_referenced_locally
  provideStore(comparisonView(useStore(), side));
</script>

<StartMenu {copy} {links} />
<Toasts {copy} running={false} still />
<Taskbar {copy} {lang} {download} {version} {classicHref} phone={false} />
