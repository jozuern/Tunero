<script lang="ts">
  // The app's rating mark: the number and five bars, filled up to it.
  let { value, label }: { value: number; label: string } = $props();
</script>

<span class="rating" title={label} aria-label={label}>
  <span class="number" aria-hidden="true">{value}</span>
  <span class="bars" aria-hidden="true">
    {#each [1, 2, 3, 4, 5] as bar (bar)}<span class:on={bar <= value}></span>{/each}
  </span>
</span>

<style>
  .rating {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    flex: none;
  }

  .number {
    font-family: var(--font-display);
    font-size: 20px;
    font-weight: 300;
    min-width: 12px;
    text-align: right;
  }

  .bars {
    display: flex;
    gap: 2px;
  }

  .bars span {
    width: 4px;
    height: 12px;
    border-radius: 1px;
    background: var(--stroke-strong);
  }

  .bars .on {
    background: var(--accent);
  }
</style>
