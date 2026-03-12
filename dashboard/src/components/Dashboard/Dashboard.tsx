import { Show } from 'solid-js';
import type {
  ActivityEntry,
  FileError,
  LanguageCount,
  OperationSnapshot,
  PipelineState,
  PipelineStageLoad,
  QueryEntry,
  SourceSection,
} from '../../types';
import { ActivityStream } from '../ActivityStream';
import { ErrorPanel } from '../ErrorPanel';
import { FileTreemap } from '../FileTreemap';
import { LanguageSpectrum } from '../LanguageSpectrum';
import { OperationTracker } from '../OperationTracker';
import { PipelinePressure } from '../PipelinePressure';
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
  stages: PipelineStageLoad[];
  writerPending: boolean;
  now: number;
}

export function Dashboard(props: DashboardProps) {
  const complete = () => props.pipeline.phase === 'complete';

  return (
    <div class="dashboard">
      <StatusHeader title={props.title} phase={props.pipeline.phase} elapsed={props.elapsed} />

      <div class="dashboard-main">
        <div class="dashboard-sankey">
          <QueryActivity entries={props.queries} variant="hero" now={props.now} />
        </div>

        <div class="dashboard-treemap">
          <FileTreemap
            sections={props.sections}
            stats={{
              total: props.pipeline.discovered,
              pending: Math.max(0, props.pipeline.total - props.pipeline.parsed),
              parsed: Math.max(0, props.pipeline.parsed - props.pipeline.structEmbedded),
              ready: Math.max(0, props.pipeline.structEmbedded - props.pipeline.fullEmbedded),
              indexed: props.pipeline.fullEmbedded,
              failed: props.pipeline.failed,
            }}
          />
        </div>

        <div class="dashboard-sidebar">
          <div class="dashboard-sidebar-main">
            <ProgressRings
              total={props.pipeline.total}
              parsed={props.pipeline.parsed}
              structEmbedded={props.pipeline.structEmbedded}
              fullEmbedded={props.pipeline.fullEmbedded}
              complete={complete()}
            />
            <PipelinePressure stages={props.stages} writerPending={props.writerPending} />
            <ErrorPanel errors={props.errors} />
            <ActivityStream entries={props.activities} />
            <LanguageSpectrum languages={props.languages} />
          </div>

          <Show when={props.operations.length > 0}>
            <div class="dashboard-sidebar-footer">
              <OperationTracker operations={props.operations} now={props.now} />
            </div>
          </Show>
        </div>
      </div>

      <Show when={complete()}>
        <div class="done-wash show" />
      </Show>
    </div>
  );
}
