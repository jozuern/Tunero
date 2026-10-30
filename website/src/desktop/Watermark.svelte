<script lang="ts">
  // The demo label, in the taskbar on wide screens and in the status bar on phones: always visible, never over a
  // window's title bar.
  let { lines, wrap = false }: { lines: string[]; wrap?: boolean } = $props();
</script>

<p class="watermark" class:wrap>
  {#each lines as line (line)}<span>{line}</span>{/each}
</p>

<style>
  .watermark {
    display: flex;
    flex-direction: column;
    justify-content: center;
    margin: 0;
    color: var(--ink-2);
    font-size: 11px;
    line-height: 1.3;
    white-space: nowrap;
    overflow: hidden;
  }

  .wrap {
    white-space: normal;
  }

  span {
    overflow: hidden;
    text-overflow: ellipsis;
  }

  span:first-child {
    color: var(--ink);
  }
</style>
