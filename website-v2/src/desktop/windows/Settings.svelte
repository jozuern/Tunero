<script lang="ts">
  // Settings of the demo PC: the display's refresh rate (what Tunero's Fix changes), and the site's own choices:
  // theme, language and animation effects.
  import type { DesktopStrings } from '../../i18n/desktop-en.ts';
  import { useStore } from '../store.svelte.ts';
  import { reducedMotion, setMotionChoice } from '../motion.ts';
  import Icon from '../Icon.svelte';
  import Toggle from '../app/Toggle.svelte';
  import type { IconName } from '../icons.ts';

  type Section = 'system' | 'personalization' | 'language' | 'accessibility';

  let { copy, lang, otherHref }: { copy: DesktopStrings['settings']; lang: 'en' | 'de'; otherHref: string } = $props();
  const store = useStore();

  let section = $state<Section>('personalization');
  const sections: { id: Section; icon: IconName }[] = [
    { id: 'system', icon: 'desktop' },
    { id: 'personalization', icon: 'star' },
    { id: 'language', icon: 'globe' },
    { id: 'accessibility', icon: 'person' },
  ];

  const refresh = store.demo.findings.find((f) => f.fix === 'apply')!;
  const fixed = $derived(store.applied({ kind: 'finding', id: refresh.id }));

  let theme = $state(typeof document === 'undefined' ? 'dark' : (document.documentElement.dataset.theme ?? 'dark'));
  function setTheme(value: 'light' | 'dark') {
    theme = value;
    document.documentElement.dataset.theme = value;
    try {
      localStorage.setItem('theme', value);
    } catch {
      // Storage blocked: the theme holds until the page is left.
    }
  }

  let motion = $state(!reducedMotion());
  function setMotion(value: boolean) {
    motion = value;
    setMotionChoice(value ? 'on' : 'off');
  }
</script>

<div class="settings">
  <nav aria-label={copy.nav.system}>
    {#each sections as item (item.id)}
      <button type="button" class:current={section === item.id} aria-current={section === item.id ? 'page' : undefined} onclick={() => (section = item.id)}>
        <Icon name={item.icon} size={16} />{copy.nav[item.id]}
      </button>
    {/each}
  </nav>
  <div class="page">
    <h1>{copy.nav[section]}</h1>
    {#if section === 'system'}
      <section class="card">
        <h2>{copy.display}</h2>
        <div class="row">
          <span>{copy.refresh}</span>
          <strong data-anchor="settings">{(fixed ? refresh.writes[0].to : refresh.writes[0].from)}</strong>
        </div>
        {#if fixed}<p class="hint">{copy.refreshHint}</p>{/if}
      </section>
    {:else if section === 'personalization'}
      <section class="card">
        <h2>{copy.theme}</h2>
        <div class="modes">
          {#each ['light', 'dark'] as const as mode (mode)}
            <button type="button" class="mode {mode}" class:current={theme === mode} aria-pressed={theme === mode} onclick={() => setTheme(mode)}>
              <span class="swatch"></span>{copy[mode]}
            </button>
          {/each}
        </div>
      </section>
    {:else if section === 'language'}
      <section class="card">
        <h2>{copy.language}</h2>
        <div class="modes">
          <a class="mode" class:current={lang === 'en'} href={lang === 'en' ? undefined : otherHref} hreflang="en" lang="en" aria-current={lang === 'en' ? 'true' : undefined}>English</a>
          <a class="mode" class:current={lang === 'de'} href={lang === 'de' ? undefined : otherHref} hreflang="de" lang="de" aria-current={lang === 'de' ? 'true' : undefined}>Deutsch</a>
        </div>
      </section>
    {:else}
      <section class="card">
        <div class="row">
          <span>{copy.animation}</span>
          <Toggle checked={motion} label={copy.animation} on={copy.on} off={copy.off} onchange={setMotion} />
        </div>
        <p class="hint">{copy.animationHint}</p>
      </section>
    {/if}
  </div>
</div>

<style>
  .settings {
    display: flex;
    flex: 1;
    min-height: 0;
  }

  nav {
    display: flex;
    flex-direction: column;
    gap: 2px;
    width: 220px;
    flex: none;
    padding: 8px;
  }

  nav button {
    display: flex;
    align-items: center;
    gap: 12px;
    height: 36px;
    padding: 0 12px;
    border: none;
    border-radius: var(--radius-sm);
    background: none;
    color: var(--ink);
    font: inherit;
    text-align: left;
  }

  nav button:hover,
  nav button.current {
    background: var(--stroke);
  }

  .page {
    flex: 1;
    min-width: 0;
    overflow: auto;
    padding: 16px 24px;
    background: var(--surface);
    border-top-left-radius: var(--radius);
  }

  h1 {
    margin: 0 0 16px;
    font-family: var(--font-display);
    font-size: 28px;
    font-weight: 600;
  }

  .card {
    padding: 16px 20px;
    border: 1px solid var(--stroke);
    border-radius: var(--radius);
    background: var(--surface-2);
  }

  h2 {
    margin: 0 0 12px;
    font-size: 14px;
    font-weight: 600;
  }

  .row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
  }

  .row strong {
    padding: 4px 12px;
    border: 1px solid var(--stroke-strong);
    border-radius: var(--radius-sm);
    font-weight: 400;
  }

  .hint {
    margin: 12px 0 0;
    color: var(--ink-2);
    font-size: 12px;
  }

  .modes {
    display: flex;
    gap: 12px;
    flex-wrap: wrap;
  }

  .mode {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 8px;
    min-width: 120px;
    padding: 12px;
    border: 2px solid var(--stroke);
    border-radius: var(--radius);
    background: none;
    color: var(--ink);
    font: inherit;
    text-decoration: none;
  }

  .mode.current {
    border-color: var(--accent);
  }

  .swatch {
    width: 96px;
    height: 56px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--stroke-strong);
  }

  .light .swatch {
    background: linear-gradient(135deg, #f3f3f3 50%, #ffffff 50%);
  }

  .dark .swatch {
    background: linear-gradient(135deg, #202020 50%, #2c2c2c 50%);
  }

  @media (max-width: 899px) {
    nav {
      width: 52px;
      padding: 4px;
    }

    nav button {
      font-size: 0;
      justify-content: center;
      padding: 0;
    }
  }
</style>
