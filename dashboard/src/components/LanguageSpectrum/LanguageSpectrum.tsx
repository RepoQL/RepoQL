import { createMemo, For, Show } from 'solid-js';
import type { LanguageCount } from '../../types';
import './LanguageSpectrum.css';

export interface LanguageSpectrumProps {
  languages: LanguageCount[];
}

export function LanguageSpectrum(props: LanguageSpectrumProps) {
  const activeLanguages = createMemo(() => props.languages.filter((language) => language.count > 0));
  const legendItems = createMemo(() => activeLanguages().slice(0, 4));
  const remaining = createMemo(() => Math.max(0, activeLanguages().length - legendItems().length));

  return (
    <div class="spectrum-section">
      <div class="spectrum-header">
        <span class="spectrum-label">Composition</span>
        <span class="spectrum-meta">{activeLanguages().length} langs</span>
      </div>
      <div class="spectrum-bar">
        <For each={activeLanguages()}>
          {(language) => (
            <div
              class="spec-seg"
              style={{ flex: language.fraction, background: language.lang.color }}
              title={`${language.lang.name} ${Math.round(language.fraction * 100)}%`}
            />
          )}
        </For>
      </div>
      <div class="spectrum-legend">
        <For each={legendItems()}>
          {(language) => (
            <div class="spec-item">
              <span class="spec-dot" style={{ background: language.lang.color }} />
              <span class="spec-name">{language.lang.name}</span>
              <span class="spec-share">{Math.round(language.fraction * 100)}%</span>
            </div>
          )}
        </For>
      </div>
      <Show when={remaining() > 0}>
        <div class="spec-more">+{remaining()} more</div>
      </Show>
    </div>
  );
}
