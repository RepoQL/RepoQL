import { createMemo, For, Show } from 'solid-js';
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

export function ClientLeases(props: ClientLeasesProps) {
  const active = createMemo(() => props.clients.filter((client) => client.activeRequest !== null));
  const idle = createMemo(() => props.clients.filter((client) => client.activeRequest === null));

  return (
    <div class="client-leases">
      <div class="cl-header">
        <span class="cl-title">Connected Clients</span>
        <div class="cl-counts">
          <span class="cl-active-count">{active().length} active</span>
          <span class="cl-total-count">{props.clients.length} total</span>
        </div>
      </div>

      <Show when={props.clients.length === 0}>
        <div class="cl-empty">No clients connected</div>
      </Show>

      <div class="cl-list">
        <For each={active()}>
          {(client) => (
            <ClientRow client={client} now={props.now} />
          )}
        </For>
        <For each={idle()}>
          {(client) => (
            <ClientRow client={client} now={props.now} />
          )}
        </For>
      </div>
    </div>
  );
}

function ClientRow(props: { client: ClientLease; now: number }) {
  const req = () => props.client.activeRequest;
  const sessionDuration = createMemo(() => formatDuration(props.now - props.client.connectedAt));

  return (
    <div class={`cl-row ${req() ? 'has-request' : 'idle'}`}>
      <div class="cl-row-top">
        <div class="cl-client-info">
          <span class={`cl-dot ${req() ? 'active' : ''}`} />
          <span class="cl-name">{props.client.name}</span>
          <span class="cl-session-id">{props.client.id.slice(0, 8)}</span>
        </div>
        <span class="cl-uptime">{sessionDuration()}</span>
      </div>

      <Show when={req()}>
        {(request) => (
          <div class="cl-request">
            <div class="cl-req-tool" style={{ color: TOOL_COLORS[request().tool] ?? 'var(--fg2)' }}>
              {request().tool}
            </div>
            <div class="cl-req-params">{request().params}</div>
            <div class="cl-req-meta">
              <span class="cl-req-elapsed">{formatDuration(props.now - request().startedAt)}</span>
              <span class="cl-req-budget">{request().tokenBudget}t</span>
            </div>
            <div class="cl-req-bar">
              <div class="cl-req-bar-fill" />
            </div>
          </div>
        )}
      </Show>

      <div class="cl-row-bottom">
        <span class="cl-stat">{props.client.requestCount} requests</span>
        <span class="cl-stat">{formatTokens(props.client.totalTokensUsed)} tokens</span>
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
