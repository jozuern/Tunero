<script lang="ts">
  import type { WindowId } from '../lib/demo-state.ts';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { useStore } from './store.svelte.ts';
  import Icon from './Icon.svelte';
  import TuneroMark from './TuneroMark.svelte';
  import type { IconName } from './icons.ts';

  let { copy }: { copy: DesktopStrings } = $props();
  const store = useStore();

  const items: { id: WindowId; icon: IconName | 'tunero'; label: string }[] = $derived([
    { id: 'tunero', icon: 'tunero', label: copy.icons.tunero },
    { id: 'trust', icon: 'folder', label: copy.icons.trust },
    { id: 'notepad', icon: 'notepad', label: copy.icons.notepad },
    { id: 'settings', icon: 'settings', label: copy.icons.settings },
  ]);
  /** The Tunero icon asks to be opened until it was opened once. */
  let opened = $state(false);
</script>

<ul class="icons">
  {#each items as item (item.id)}
    <li>
      <button
        type="button"
        class:hint={item.id === 'tunero' && !opened && !store.windows.tunero.open}
        onclick={() => {
          if (item.id === 'tunero') opened = true;
          store.open(item.id);
        }}
      >
        <span class="glyph">
          {#if item.icon === 'tunero'}<TuneroMark size={44} />{:else}<Icon name={item.icon} size={44} />{/if}
        </span>
        <span class="label">{item.label}</span>
        {#if item.id === 'tunero' && !opened && !store.windows.tunero.open}
          <span class="tip">{copy.startHere}</span>
        {/if}
      </button>
    </li>
  {/each}
</ul>

<style>
  .icons {
    position: absolute;
    top: 12px;
    left: 8px;
    z-index: 1;
    display: grid;
    gap: 8px;
    margin: 0;
    padding: 0;
    list-style: none;
  }

  button {
    position: relative;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 4px;
    width: 84px;
    padding: 8px 4px;
    border: 1px solid transparent;
    border-radius: var(--radius-sm);
    background: transparent;
    color: #fff;
    font: inherit;
    font-size: 12px;
    text-shadow: 0 1px 3px rgba(0, 0, 0, 0.8);
  }

  button:hover {
    background: rgba(255, 255, 255, 0.12);
    border-color: rgba(255, 255, 255, 0.18);
  }

  .glyph {
    display: grid;
    place-items: center;
    width: 48px;
    height: 48px;
    color: #ffd86b;
    filter: drop-shadow(0 2px 4px rgba(0, 0, 0, 0.35));
  }

  li:nth-child(3) .glyph,
  li:nth-child(4) .glyph {
    color: #f3f3f3;
  }

  .hint .glyph {
    animation: pulse 2.4s ease-in-out infinite;
  }

  @keyframes pulse {
    50% {
      transform: scale(1.1);
      filter: drop-shadow(0 0 14px rgba(96, 205, 255, 0.9));
    }
  }

  .tip {
    position: absolute;
    left: calc(100% + 8px);
    top: 20px;
    padding: 4px 10px;
    border-radius: var(--radius-sm);
    background: var(--accent);
    color: var(--accent-ink);
    font-weight: 600;
    white-space: nowrap;
    text-shadow: none;
    box-shadow: var(--shadow);
  }

  .tip::before {
    content: '';
    position: absolute;
    left: -5px;
    top: 50%;
    width: 10px;
    height: 10px;
    background: inherit;
    transform: translateY(-50%) rotate(45deg);
  }

  @media (prefers-reduced-motion: reduce) {
    .hint .glyph {
      animation: none;
    }
  }
</style>
