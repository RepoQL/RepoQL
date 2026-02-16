import type { ClientLease } from '../../types';
import './ClientLeases.css';

export interface ClientLeasesProps {
  clients: ClientLease[];
  /** Current time for computing durations — pass Date.now() from a tick */
  now: number;
}

const TOOL_COLORS: Record<string, string> = {
  explore: 'var(--blue)',
  explain: 'var(--amber)',
  query: 'var(--green)',
  read: 'var(--fg2)',
};

export function ClientLeases({ clients, now }: ClientLeasesProps) {
  const active = clients.filter((c) => c.activeRequest !== null);
  const idle = clients.filter((c) => c.activeRequest === null);

  return (
    <div className="client-leases">
      <div className="cl-header">
        <span className="cl-title">Connected Clients</span>
        <div className="cl-counts">
          <span className="cl-active-count">{active.length} active</span>
          <span className="cl-total-count">{clients.length} total</span>
        </div>
      </div>

      {clients.length === 0 && (
        <div className="cl-empty">No clients connected</div>
      )}

      <div className="cl-list">
        {active.map((client) => (
          <ClientRow key={client.id} client={client} now={now} />
        ))}
        {idle.map((client) => (
          <ClientRow key={client.id} client={client} now={now} />
        ))}
      </div>
    </div>
  );
}

function ClientRow({ client, now }: { client: ClientLease; now: number }) {
  const req = client.activeRequest;
  const sessionDuration = formatDuration(now - client.connectedAt);

  return (
    <div className={`cl-row ${req ? 'has-request' : 'idle'}`}>
      <div className="cl-row-top">
        <div className="cl-client-info">
          <span className={`cl-dot ${req ? 'active' : ''}`} />
          <span className="cl-name">{client.name}</span>
          <span className="cl-session-id">{client.id.slice(0, 8)}</span>
        </div>
        <span className="cl-uptime">{sessionDuration}</span>
      </div>

      {req && (
        <div className="cl-request">
          <div className="cl-req-tool" style={{ color: TOOL_COLORS[req.tool] ?? 'var(--fg2)' }}>
            {req.tool}
          </div>
          <div className="cl-req-params">{req.params}</div>
          <div className="cl-req-meta">
            <span className="cl-req-elapsed">{formatDuration(now - req.startedAt)}</span>
            <span className="cl-req-budget">{req.tokenBudget}t</span>
          </div>
          <div className="cl-req-bar">
            <div className="cl-req-bar-fill" />
          </div>
        </div>
      )}

      <div className="cl-row-bottom">
        <span className="cl-stat">{client.requestCount} requests</span>
        <span className="cl-stat">{formatTokens(client.totalTokensUsed)} tokens</span>
      </div>
    </div>
  );
}

function formatDuration(ms: number): string {
  const sec = Math.floor(ms / 1000);
  if (sec < 60) return `${sec}s`;
  const min = Math.floor(sec / 60);
  const remSec = sec % 60;
  if (min < 60) return `${min}m ${remSec}s`;
  const hr = Math.floor(min / 60);
  return `${hr}h ${min % 60}m`;
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M';
  if (n >= 1_000) return (n / 1_000).toFixed(1) + 'K';
  return n.toString();
}
