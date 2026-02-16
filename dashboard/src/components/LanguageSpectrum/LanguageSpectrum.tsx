import type { LanguageCount } from '../../types';
import './LanguageSpectrum.css';

export interface LanguageSpectrumProps {
  languages: LanguageCount[];
}

export function LanguageSpectrum({ languages }: LanguageSpectrumProps) {
  return (
    <div className="spectrum-section">
      <div className="spectrum-label">Languages</div>
      <div className="spectrum-bar">
        {languages.map(({ ext, lang, fraction }) => (
          <div
            key={ext}
            className="spec-seg"
            style={{ flex: fraction, background: lang.color, opacity: 0.7 }}
          />
        ))}
      </div>
      <div className="spectrum-legend">
        {languages
          .filter((l) => l.fraction > 0.04)
          .map(({ ext, lang, fraction }) => (
            <div key={ext} className="spec-item">
              <span className="spec-dot" style={{ background: lang.color }} />
              {lang.name} {Math.round(fraction * 100)}%
            </div>
          ))}
      </div>
    </div>
  );
}
