<script lang="ts">
  import { icons, type IconName } from './icons.ts';

  let { name, size = 20 }: { name: IconName; size?: number } = $props();
</script>

<span class="icon" style:--size="{size}px" aria-hidden="true">{@html icons[name]}</span>

<style>
  .icon {
    display: inline-flex;
    width: var(--size);
    height: var(--size);
    flex: none;
  }

  .icon :global(svg) {
    width: 100%;
    height: 100%;
    fill: currentColor;
  }
</style>
