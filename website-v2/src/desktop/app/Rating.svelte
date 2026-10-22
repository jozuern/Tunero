<script lang="ts">
  // The app's rating mark: the number and five bars, filled up to it. Like the app, a 0 shows no mark but keeps its place,
  // so the marks of a list stand in one column.
  let { value, label }: { value: number; label: string } = $props();
</script>

<!-- An image to screen readers, named by its label ("Impact 4 of 5"); a 0 shows no mark and says nothing. -->
<span class="rating" class:none={value <= 0} title={label} role={value > 0 ? 'img' : undefined} aria-label={value > 0 ? label : undefined} aria-hidden={value > 0 ? undefined : 'true'}>
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

  .none {
    visibility: hidden;
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
