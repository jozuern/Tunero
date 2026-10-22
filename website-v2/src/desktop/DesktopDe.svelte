<script lang="ts">
  // The German island: the desktop with its data from a cached module (lib/desktop-data.ts), not from the page's HTML.
  import data from 'virtual:desktop-data/de';
  import Desktop, { type Facts } from './Desktop.svelte';

  let { facts }: { facts: Facts } = $props();
</script>

<Desktop {...data} {facts} />
