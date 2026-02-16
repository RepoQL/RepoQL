import { useRef, useEffect, useCallback } from 'react';
import type { PipelineState } from '../../types';
import './PipelineSankey.css';

export interface PipelineSankeyProps {
  pipeline: PipelineState;
}

interface Stage {
  label: string;
  val: number;
  xFrac: number;
  color: string;
  activePhases: string[];
}

const STAGES: Omit<Stage, 'val'>[] = [
  { label: 'Discovered', xFrac: 0.025, color: 'var(--fg3)', activePhases: ['discovery'] },
  { label: 'Classified', xFrac: 0.2, color: 'var(--fg2)', activePhases: ['classifying'] },
  { label: 'Parsed', xFrac: 0.4, color: 'var(--blue)', activePhases: ['parsing'] },
  { label: 'Searchable', xFrac: 0.6, color: 'var(--amber)', activePhases: ['struct_embedding'] },
  { label: 'Full Embed', xFrac: 0.8, color: 'var(--green)', activePhases: ['full_embedding'] },
];

export function PipelineSankey({ pipeline }: PipelineSankeyProps) {
  const svgRef = useRef<SVGSVGElement>(null);

  const render = useCallback(() => {
    const svg = svgRef.current;
    if (!svg) return;

    const box = svg.getBoundingClientRect();
    const W = box.width || 800;
    const H = box.height || 120;
    svg.setAttribute('viewBox', `0 0 ${W} ${H}`);

    const maxH = H - 40;
    const barW = 24;
    const total = pipeline.total || 1;
    const vals = [pipeline.discovered, pipeline.classified, pipeline.parsed, pipeline.structEmbedded, pipeline.fullEmbedded];

    let html = '';
    for (let i = 0; i < STAGES.length; i++) {
      const s = STAGES[i]!;
      const val = vals[i]!;
      const x = s.xFrac * W;
      const h = Math.max(3, (val / total) * maxH);
      const y = (H - h) / 2;

      // Bar
      html += `<rect x="${x}" y="${y}" width="${barW}" height="${h}" fill="${s.color}" opacity=".5" rx="2"/>`;

      // Value
      const isActive = s.activePhases.includes(pipeline.phase);
      html += `<text class="sk-val${isActive ? ' active' : ''}" x="${x + barW / 2}" y="${y - 6}" text-anchor="middle">${val}</text>`;
      html += `<text class="sk-label" x="${x + barW / 2}" y="${H - 4}" text-anchor="middle">${s.label}</text>`;

      // Flow to next
      if (i < STAGES.length - 1) {
        const nextVal = vals[i + 1]!;
        const nextX = STAGES[i + 1]!.xFrac * W;
        const h1 = Math.max(3, (nextVal / total) * maxH);
        const y1 = (H - h1) / 2;
        const x0 = x + barW;
        const cx = (x0 + nextX) / 2;
        const nextColor = STAGES[i + 1]!.color;
        html += `<path d="M${x0},${y} C${cx},${y} ${cx},${y1} ${nextX},${y1} L${nextX},${y1 + h1} C${cx},${y1 + h1} ${cx},${y + h} ${x0},${y + h} Z" fill="${nextColor}" opacity=".08"/>`;
      }
    }

    // Failed indicator
    if (pipeline.failed > 0) {
      html += `<text class="sk-label" x="${W - 20}" y="${H / 2 + 4}" text-anchor="end" fill="var(--red)">${pipeline.failed} failed</text>`;
    }

    svg.innerHTML = html;
  }, [pipeline]);

  useEffect(() => {
    render();
    window.addEventListener('resize', render);
    return () => window.removeEventListener('resize', render);
  }, [render]);

  return (
    <div className="sankey-area">
      <svg ref={svgRef} className="sankey-svg" preserveAspectRatio="xMidYMid meet" />
    </div>
  );
}
