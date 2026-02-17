import { Show } from 'solid-js';
import type { HostHealth, HostStatus } from '../../types';
import './ConnectionStatus.css';

export interface ConnectionStatusProps {
  health: HostHealth;
}

const STATUS_CONFIG: Record<HostStatus, { label: string; class: string }> = {
  connected: { label: 'Connected', class: 'cs-dot connected' },
  disconnected: { label: 'Disconnected', class: 'cs-dot disconnected' },
  reconnecting: { label: 'Reconnecting', class: 'cs-dot reconnecting' },
};

export function ConnectionStatus(props: ConnectionStatusProps) {
  const config = () => STATUS_CONFIG[props.health.status];

  return (
    <div class="connection-status">
      <div class={config().class} />
      <span class="cs-label">{config().label}</span>
      <Show when={props.health.status === 'connected'}>
        <>
          <span class="cs-sep" />
          <span class="cs-latency">{props.health.latencyMs}ms</span>
          <span class="cs-sep" />
          <span class="cs-version">{props.health.version}</span>
        </>
      </Show>
    </div>
  );
}
