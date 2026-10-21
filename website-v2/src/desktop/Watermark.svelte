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

  :global([data-theme='light']) .watermark {
    color: rgba(0, 0, 0, 0.62);
    text-shadow: 0 1px 2px rgba(255, 255, 255, 0.6);
  }

  @media (max-width: 899px) {
    .watermark {
      top: 52px;
      right: 12px;
      font-size: 11px;
    }

    span:first-child {
      font-size: 13px !important;
    }
  }

  span:first-child {
    font-size: 16px;
  }
</style>
