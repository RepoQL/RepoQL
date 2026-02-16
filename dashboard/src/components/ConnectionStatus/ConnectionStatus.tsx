import type { HostHealth, HostStatus } from '../../types';
import './ConnectionStatus.css';

export interface ConnectionStatusProps {
  health: HostHealth;
}

const STATUS_CONFIG: Record<HostStatus, { label: string; className: string }> = {
  connected: { label: 'Connected', className: 'cs-dot connected' },
  disconnected: { label: 'Disconnected', className: 'cs-dot disconnected' },
  reconnecting: { label: 'Reconnecting', className: 'cs-dot reconnecting' },
};

export function ConnectionStatus({ health }: ConnectionStatusProps) {
  const config = STATUS_CONFIG[health.status];

  return (
    <div className="connection-status">
      <div className={config.className} />
      <span className="cs-label">{config.label}</span>
      {health.status === 'connected' && (
        <>
          <span className="cs-sep" />
          <span className="cs-latency">{health.latencyMs}ms</span>
          <span className="cs-sep" />
          <span className="cs-version">{health.version}</span>
        </>
      )}
    </div>
  );
}
