import type { PipelineState, ActivityEntry, FileGroup, LanguageCount } from '../../types';
import { StatusHeader } from '../StatusHeader';
import { FileTreemap } from '../FileTreemap';
import { ProgressRings } from '../ProgressRings';
import { PhaseIndicator } from '../PhaseIndicator';
import { LanguageSpectrum } from '../LanguageSpectrum';
import { ActivityStream } from '../ActivityStream';
import { PipelineSankey } from '../PipelineSankey';
import './Dashboard.css';

export interface DashboardProps {
  title: string;
  elapsed: number;
  pipeline: PipelineState;
  groups: FileGroup[];
  languages: LanguageCount[];
  activities: ActivityEntry[];
}

export function Dashboard({ title, elapsed, pipeline, groups, languages, activities }: DashboardProps) {
  const complete = pipeline.phase === 'complete';

  return (
    <div className="dashboard">
      <StatusHeader title={title} phase={pipeline.phase} elapsed={elapsed} />

      <div className="dashboard-main">
        <div className="dashboard-treemap">
          <FileTreemap
            groups={groups}
            stats={{
              total: pipeline.discovered,
              classified: pipeline.classified,
              parsed: pipeline.parsed,
              searchable: pipeline.structEmbedded,
            }}
          />
        </div>

        <div className="dashboard-sidebar">
          <ProgressRings
            total={pipeline.total}
            parsed={pipeline.parsed}
            structEmbedded={pipeline.structEmbedded}
            fullEmbedded={pipeline.fullEmbedded}
            complete={complete}
          />
          <PhaseIndicator phase={pipeline.phase} />
          <LanguageSpectrum languages={languages} />
          <ActivityStream entries={activities} />
        </div>

        <div className="dashboard-sankey">
          <PipelineSankey pipeline={pipeline} />
        </div>
      </div>

      {complete && <div className="done-wash show" />}
    </div>
  );
}
