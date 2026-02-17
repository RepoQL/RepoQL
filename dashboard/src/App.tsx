import { createSignal, onMount, onCleanup, Show } from 'solid-js';
import { Dashboard } from './components/Dashboard/Dashboard';
import { useRepoQLDashboard } from './hooks/useRepoQLDashboard';
import './theme.css';

function useSingletonTab() {
  const [isPrimary, setIsPrimary] = createSignal(false);
  let isPrimaryRef = false;

  onMount(() => {
    const bc = new BroadcastChannel('repoql-dashboard');

    bc.postMessage({ type: 'ping' });

    let gotPong = false;
    const timeout = setTimeout(() => {
      if (!gotPong) {
        isPrimaryRef = true;
        setIsPrimary(true);
      }
    }, 300);

    bc.onmessage = (e) => {
      if (e.data?.type === 'pong') {
        gotPong = true;
        clearTimeout(timeout);
        isPrimaryRef = false;
        setIsPrimary(false);
      } else if (e.data?.type === 'ping') {
        if (isPrimaryRef) {
          bc.postMessage({ type: 'pong' });
          window.focus();
        }
      }
    };

    onCleanup(() => {
      clearTimeout(timeout);
      bc.close();
    });
  });

  return isPrimary;
}

export default function App() {
  const isPrimary = useSingletonTab();
  const dashboard = useRepoQLDashboard();

  return (
    <Show
      when={isPrimary()}
      fallback={
        <div
          style={{
            display: 'flex',
            'flex-direction': 'column',
            'align-items': 'center',
            'justify-content': 'center',
            height: '100vh',
            gap: '12px',
            color: 'var(--fg3)',
            'font-family': 'var(--font-mono)',
            'font-size': '0.8rem',
          }}
        >
          <div>Dashboard is already open in another tab.</div>
          <div style={{ color: 'var(--fg4)', 'font-size': '0.65rem' }}>The existing tab has been activated.</div>
        </div>
      }
    >
      <Show
        when={dashboard.props()}
        fallback={
          <div
            style={{
              display: 'flex',
              'align-items': 'center',
              'justify-content': 'center',
              height: '100vh',
              color: 'var(--fg3)',
              'font-family': 'var(--font-mono)',
              'font-size': '0.8rem',
            }}
          >
            {dashboard.error()
              ? `Error: ${dashboard.error()}`
              : dashboard.connected()
                ? 'Connected - waiting for snapshot...'
                : 'Connecting to host...'}
          </div>
        }
      >
        {(p) => <Dashboard {...p()} />}
      </Show>
    </Show>
  );
}
