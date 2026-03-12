import { createMemo, For, Show } from 'solid-js';
import type { PipelineStageLoad } from '../../types';
import './PipelinePressure.css';

export interface PipelinePressureProps {
  stages: PipelineStageLoad[];
  writerPending: boolean;
}

interface StageStatus {
  name: string;
  label: string;
  queued: number;
  inProgress: number;
  busy: boolean;
  total: number;
  tone: 'blocked' | 'active' | 'idle';
}

export function PipelinePressure(props: PipelinePressureProps) {
  const stageStatuses = createMemo<StageStatus[]>(() =>
    props.stages
      .map((stage) => {
        const total = stage.queued + stage.inProgress;
        const tone: StageStatus['tone'] =
          stage.queued > 0 ? 'blocked' :
          (stage.inProgress > 0 || stage.busy) ? 'active' :
          'idle';

        return {
          name: stage.name,
          label: formatStageName(stage.name),
          queued: stage.queued,
          inProgress: stage.inProgress,
          busy: stage.busy,
          total,
          tone,
        };
      })
      .sort((a, b) =>
        toneRank(a.tone) - toneRank(b.tone)
        || b.queued - a.queued
        || b.inProgress - a.inProgress
        || a.label.localeCompare(b.label)),
  );

  const headline = createMemo(() => {
    const blocked = stageStatuses().find((stage) => stage.queued > 0);
    if (blocked) {
      return `${blocked.label} is backing up`;
    }

    const active = stageStatuses().find((stage) => stage.inProgress > 0 || stage.busy);
    if (active) {
      return `${active.label} is running`;
    }

    if (props.writerPending) {
      return 'Writer is still flushing';
    }

    return 'No pipeline pressure';
  });

  const blockedCount = createMemo(() =>
    stageStatuses().reduce((sum, stage) => sum + stage.queued, 0),
  );

  const workingCount = createMemo(() =>
    stageStatuses().reduce((sum, stage) => sum + stage.inProgress, 0),
  );

  const visibleStages = createMemo(() =>
    stageStatuses().filter((stage) => stage.queued > 0 || stage.inProgress > 0 || stage.busy),
  );

  return (
    <div class="pipeline-pressure">
      <div class="pp-header">
        <div>
          <div class="pp-title">Pipeline</div>
          <div class="pp-headline">{headline()}</div>
        </div>
        <div class="pp-overview">
          <span class="pp-overview-chip blocked">{blockedCount()} queued</span>
          <span class="pp-overview-chip active">{workingCount()} running</span>
        </div>
      </div>

      <div class="pp-list">
        <Show when={visibleStages().length > 0} fallback={<div class="pp-empty">Nothing is waiting in the pipeline.</div>}>
          <For each={visibleStages()}>
            {(stage) => (
              <div class={`pp-row ${stage.tone}`}>
                <div class="pp-row-main">
                  <span class="pp-stage">{stage.label}</span>
                  <span class="pp-status">{describeStage(stage)}</span>
                </div>
                <div class="pp-bar">
                  <div class="pp-bar-track">
                    <div class="pp-bar-queued" style={{ width: `${barWidth(stage.queued, stage.total)}%` }} />
                    <div class="pp-bar-running" style={{ width: `${barWidth(stage.inProgress, stage.total)}%` }} />
                  </div>
                  <div class="pp-metrics">
                    <span>Q {stage.queued}</span>
                    <span>Run {stage.inProgress}</span>
                    <Show when={stage.busy && stage.inProgress === 0}>
                      <span>Live</span>
                    </Show>
                  </div>
                </div>
              </div>
            )}
          </For>
        </Show>
      </div>

      <Show when={props.writerPending}>
        <div class="pp-footer">Writer backlog is delaying ready files.</div>
      </Show>
    </div>
  );
}

function toneRank(tone: StageStatus['tone']): number {
  switch (tone) {
    case 'blocked':
      return 0;
    case 'active':
      return 1;
    default:
      return 2;
  }
}

function describeStage(stage: StageStatus): string {
  if (stage.queued > 0) {
    return `${stage.queued} queued`;
  }

  if (stage.inProgress > 0) {
    return `${stage.inProgress} running`;
  }

  if (stage.busy) {
    return 'active';
  }

  return 'idle';
}

function barWidth(value: number, total: number): number {
  if (value <= 0 || total <= 0) {
    return 0;
  }

  return Math.max(12, Math.min(100, (value / total) * 100));
}

function formatStageName(name: string): string {
  switch (name.toLowerCase()) {
    case 'discovery':
      return 'Discovery';
    case 'parsing':
      return 'Hot path';
    case 'analysis':
      return 'Analysis';
    case 'writer':
      return 'Writer';
    default:
      return name;
  }
}
