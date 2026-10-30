<script lang="ts">
  // An <img>, not inline SVG: the mark's gradients have ids, which would clash between several inline copies.
  import mark from '../assets/tunero-icon.svg?url';

  let { size = 20 }: { size?: number } = $props();
</script>

<img src={mark} alt="" width={size} height={size} draggable="false" />

<style>
  img {
    display: block;
    flex: none;
  }
</style>
