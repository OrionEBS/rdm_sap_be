import { useCallback, useEffect, useState } from 'react';
import { loadSession, saveSession, setUnauthorizedHandler, type Session } from './api';
import LoginPage from './pages/LoginPage';
import EndpointsPage from './pages/EndpointsPage';
import DbSyncPage from './pages/DbSyncPage';
import SapCallsPage from './pages/SapCallsPage';
import type { View } from './components/Topbar';

export default function App() {
  const [session, setSession] = useState<Session | null>(() => loadSession());
  const [view, setView] = useState<View>(() =>
    window.location.hash === '#dbsync' ? 'dbsync' : window.location.hash.startsWith('#sapcalls') ? 'sapcalls' : 'endpoints'
  );
  const navigate = useCallback((v: View) => {
    setView(v);
    window.history.replaceState(null, '', v === 'endpoints' ? '#' : `#${v}`);
  }, []);
  const [notice, setNotice] = useState<string | null>(null);

  const logout = useCallback((message?: string) => {
    saveSession(null);
    setSession(null);
    setNotice(message ?? null);
  }, []);

  useEffect(() => {
    setUnauthorizedHandler(() => logout('Phiên đăng nhập hết hạn — đăng nhập lại.'));
  }, [logout]);

  // Tự đăng xuất khi token hết hạn (60 phút)
  useEffect(() => {
    if (!session) return;
    const ms = new Date(session.expiresAtUtc).getTime() - Date.now();
    const timer = window.setTimeout(() => logout('Phiên đăng nhập hết hạn — đăng nhập lại.'), Math.max(ms, 0));
    return () => window.clearTimeout(timer);
  }, [session, logout]);

  if (!session) {
    return (
      <LoginPage
        notice={notice}
        onLogin={(s) => {
          saveSession(s);
          setSession(s);
          setNotice(null);
        }}
      />
    );
  }
  if (view === 'sapcalls') return <SapCallsPage session={session} onNavigate={navigate} onLogout={() => logout()} />;
  return view === 'dbsync' ? (
    <DbSyncPage session={session} onNavigate={navigate} onLogout={() => logout()} />
  ) : (
    <EndpointsPage session={session} onNavigate={navigate} onLogout={() => logout()} />
  );
}
