import { useEffect, useRef, useState } from 'react';
import { Dashboard } from './components/Dashboard/Dashboard';
import { useRepoQLDashboard } from './hooks/useRepoQLDashboard';
import './theme.css';

function useSingletonTab(): { isPrimary: boolean } {
  const [isPrimary, setIsPrimary] = useState(false);
  const isPrimaryRef = useRef(false);

  useEffect(() => {
    const bc = new BroadcastChannel('repoql-dashboard');

    bc.postMessage({ type: 'ping' });

    let gotPong = false;
    const timeout = setTimeout(() => {
      if (!gotPong) {
        isPrimaryRef.current = true;
        setIsPrimary(true);
      }
    }, 300);

    bc.onmessage = (e) => {
      if (e.data?.type === 'pong') {
        gotPong = true;
        clearTimeout(timeout);
        isPrimaryRef.current = false;
        setIsPrimary(false);
      } else if (e.data?.type === 'ping') {
        if (isPrimaryRef.current) {
          bc.postMessage({ type: 'pong' });
          window.focus();
        }
      }
    };

    return () => {
      clearTimeout(timeout);
      bc.close();
    };
  }, []);

  return { isPrimary };
}

export default function App() {
  const { isPrimary } = useSingletonTab();
  const { props, connected, error } = useRepoQLDashboard();

  if (!isPrimary) {
    return (
      <div
        style={{
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          height: '100vh',
          gap: '12px',
          color: 'var(--fg3)',
          fontFamily: 'var(--font-mono)',
          fontSize: '0.8rem',
        }}
      >
        <div>Dashboard is already open in another tab.</div>
        <div style={{ color: 'var(--fg4)', fontSize: '0.65rem' }}>The existing tab has been activated.</div>
      </div>
    );
  }

  if (!props) {
    return (
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          height: '100vh',
          color: 'var(--fg3)',
          fontFamily: 'var(--font-mono)',
          fontSize: '0.8rem',
        }}
      >
        {error ? `Error: ${error}` : connected ? 'Connected - waiting for snapshot...' : 'Connecting to host...'}
      </div>
    );
  }

  return <Dashboard {...props} />;
}
