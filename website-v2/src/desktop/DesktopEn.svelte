<script lang="ts">
  // The English island: the desktop with its data from a cached module (lib/desktop-data.ts), not from the page's HTML.
  import data from 'virtual:desktop-data/en';
  import Desktop, { type Facts } from './Desktop.svelte';

  let { facts }: { facts: Facts } = $props();
</script>

<Desktop {...data} {facts} />
