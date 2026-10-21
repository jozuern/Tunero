<script lang="ts">
  // A Fluent toggle switch with its state text beside it: after it in the app's lists, before it in Windows Settings.
  let {
    checked,
    label,
    on,
    off,
    onchange,
    stateFirst = false,
    auto,
  }: {
    checked: boolean;
    label: string;
    on: string;
    off: string;
    onchange: (value: boolean) => void;
    stateFirst?: boolean;
    /** The guided fix's target (lib/autoplay.ts). */
    auto?: string;
  } = $props();
</script>

<label class="toggle" class:state-first={stateFirst} data-auto={auto}>
  <input type="checkbox" role="switch" {checked} aria-label={label} onchange={(event) => onchange((event.currentTarget as HTMLInputElement).checked)} />
  <span class="track" aria-hidden="true"><span class="thumb"></span></span>
  <span class="state">{checked ? on : off}</span>
</label>

<style>
  .toggle {
    display: inline-flex;
    align-items: center;
    gap: 12px;
    flex: none;
    cursor: pointer;
  }

  input {
    position: absolute;
    opacity: 0;
    width: 1px;
    height: 1px;
  }

  .track {
    position: relative;
    width: 40px;
    height: 20px;
    border: 1px solid var(--ink-2);
    border-radius: 10px;
    transition: background 150ms, border-color 150ms;
  }

  .thumb {
    position: absolute;
    top: 3px;
    left: 3px;
    width: 12px;
    height: 12px;
    border-radius: 50%;
    background: var(--ink-2);
    transition: transform 180ms cubic-bezier(0.1, 0.9, 0.2, 1), background 150ms;
  }

  input:checked + .track {
    background: var(--accent);
    border-color: var(--accent);
  }

  input:checked + .track .thumb {
    transform: translateX(20px);
    background: var(--accent-ink);
  }

  input:focus-visible + .track {
    outline: 2px solid var(--ink);
    outline-offset: 2px;
  }

  .state {
    min-width: 96px;
    font-size: 14px;
  }

  .state-first {
    flex-direction: row-reverse;
  }

  .state-first .state {
    min-width: 0;
    text-align: right;
  }

  :global([data-motion='reduce']) .track,
  :global([data-motion='reduce']) .thumb {
    transition: none;
  }
</style>
