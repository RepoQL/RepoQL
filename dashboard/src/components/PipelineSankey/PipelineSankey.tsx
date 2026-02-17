import { createEffect, onCleanup, onMount } from 'solid-js';
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
  { label: 'Indexing', xFrac: 0.2, color: 'var(--fg2)', activePhases: ['classifying'] },
  { label: 'Parsed', xFrac: 0.4, color: 'var(--blue)', activePhases: ['parsing'] },
  { label: 'Ready', xFrac: 0.6, color: 'var(--amber)', activePhases: ['struct_embedding'] },
  { label: 'Fully Indexed', xFrac: 0.8, color: 'var(--green)', activePhases: ['full_embedding'] },
];

export function PipelineSankey(props: PipelineSankeyProps) {
  let svgRef: SVGSVGElement | undefined;

  const render = () => {
    if (!svgRef) return;

    const box = svgRef.getBoundingClientRect();
    const W = box.width || 800;
    const H = box.height || 120;
    svgRef.setAttribute('viewBox', `0 0 ${W} ${H}`);

    const maxH = H - 40;
    const barW = 24;
    const total = props.pipeline.total || 1;
    const vals = [
      props.pipeline.discovered,
      props.pipeline.classified,
      props.pipeline.parsed,
      props.pipeline.structEmbedded,
      props.pipeline.fullEmbedded,
    ];

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
      const isActive = s.activePhases.includes(props.pipeline.phase);
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
    if (props.pipeline.failed > 0) {
      html += `<text class="sk-label" x="${W - 20}" y="${H / 2 + 4}" text-anchor="end" fill="var(--red)">${props.pipeline.failed} failed</text>`;
    }

    svgRef.innerHTML = html;
  };

  onMount(() => {
    render();
    window.addEventListener('resize', render);
  });

  onCleanup(() => {
    window.removeEventListener('resize', render);
  });

  createEffect(() => {
    render();
  });

  return (
    <div class="sankey-area">
      <svg ref={(el) => { svgRef = el; }} class="sankey-svg" preserveAspectRatio="xMidYMid meet" />
    </div>
  );
}
