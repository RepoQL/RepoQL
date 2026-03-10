import { Show } from 'solid-js';
import type {
  ActivityEntry,
  FileError,
  LanguageCount,
  OperationSnapshot,
  PipelineState,
  QueryEntry,
  SourceSection,
} from '../../types';
import { ActivityStream } from '../ActivityStream';
import { ErrorPanel } from '../ErrorPanel';
import { FileTreemap } from '../FileTreemap';
import { LanguageSpectrum } from '../LanguageSpectrum';
import { OperationTracker } from '../OperationTracker';
import { PipelineSankey } from '../PipelineSankey';
import { ProgressRings } from '../ProgressRings';
import { QueryActivity } from '../QueryActivity';
import { StatusHeader } from '../StatusHeader';
import './Dashboard.css';

export interface DashboardProps {
  title: string;
  elapsed: number;
  pipeline: PipelineState;
  sections: SourceSection[];
  languages: LanguageCount[];
  activities: ActivityEntry[];
  errors: FileError[];
  queries: QueryEntry[];
  operations: OperationSnapshot[];
  now: number;
}

export function Dashboard(props: DashboardProps) {
  const complete = () => props.pipeline.phase === 'complete';

  return (
    <div class="dashboard">
      <StatusHeader title={props.title} phase={props.pipeline.phase} elapsed={props.elapsed} />

      <div class="dashboard-main">
        <div class="dashboard-sankey">
          <PipelineSankey pipeline={props.pipeline} />
        </div>

        <div class="dashboard-treemap">
          <FileTreemap
            sections={props.sections}
            stats={{
              total: props.pipeline.discovered,
              classified: props.pipeline.classified,
              parsed: props.pipeline.parsed,
              searchable: props.pipeline.structEmbedded,
            }}
          />
        </div>

        <div class="dashboard-sidebar">
          <ProgressRings
            total={props.pipeline.total}
            parsed={props.pipeline.parsed}
            structEmbedded={props.pipeline.structEmbedded}
            fullEmbedded={props.pipeline.fullEmbedded}
            complete={complete()}
          />
          <Show when={props.operations.length > 0}>
            <OperationTracker operations={props.operations} now={props.now} />
          </Show>
          <ErrorPanel errors={props.errors} />
          <QueryActivity entries={props.queries} />
          <ActivityStream entries={props.activities} />
          <LanguageSpectrum languages={props.languages} />
        </div>
      </div>

      <Show when={complete()}>
        <div class="done-wash show" />
      </Show>
    </div>
  );
}
