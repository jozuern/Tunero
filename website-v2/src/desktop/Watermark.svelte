<script lang="ts">
  let { lines }: { lines: string[] } = $props();
</script>

<p class="watermark">
  {#each lines as line (line)}<span>{line}</span>{/each}
</p>

<style>
  .watermark {
    position: fixed;
    right: 24px;
    top: 16px;
    z-index: 2;
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    margin: 0;
    color: rgba(255, 255, 255, 0.72);
    font-size: 13px;
    line-height: 1.4;
    text-shadow: 0 1px 2px rgba(0, 0, 0, 0.6);
    pointer-events: none;
  }

  span:first-child {
    font-size: 16px;
  }
</style>
