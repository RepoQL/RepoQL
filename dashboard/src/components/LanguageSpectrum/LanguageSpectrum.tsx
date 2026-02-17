import { createMemo, For } from 'solid-js';
import type { LanguageCount } from '../../types';
import './LanguageSpectrum.css';

export interface LanguageSpectrumProps {
  languages: LanguageCount[];
}

export function LanguageSpectrum(props: LanguageSpectrumProps) {
  const legendItems = createMemo(() => props.languages.filter((language) => language.fraction > 0.04));

  return (
    <div class="spectrum-section">
      <div class="spectrum-label">Languages</div>
      <div class="spectrum-bar">
        <For each={props.languages}>
          {(language) => (
            <div
              class="spec-seg"
              style={{ flex: language.fraction, background: language.lang.color, opacity: 0.7 }}
            />
          )}
        </For>
      </div>
      <div class="spectrum-legend">
        <For each={legendItems()}>
          {(language) => (
            <div class="spec-item">
              <span class="spec-dot" style={{ background: language.lang.color }} />
              {language.lang.name} {Math.round(language.fraction * 100)}%
            </div>
          )}
        </For>
      </div>
    </div>
  );
}
