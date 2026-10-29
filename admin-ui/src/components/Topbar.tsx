import { useEffect, useState } from 'react';
import { api, formatTime, type Session, type SystemInfo } from '../api';

export type View = 'endpoints' | 'sapcalls' | 'dbsync';

interface Props {
  session: Session;
  view: View;
  onNavigate: (view: View) => void;
  onLogout: () => void;
}

// Đọc 1 lần cho mọi trang (đổi tab không gọi lại).
let systemCache: Promise<SystemInfo | null> | null = null;
function loadSystem(token: string) {
  systemCache ??= api.system(token).catch(() => null);
  return systemCache;
}

export default function Topbar({ session, view, onNavigate, onLogout }: Props) {
  const [sys, setSys] = useState<SystemInfo | null>(null);

  useEffect(() => {
    let alive = true;
    void loadSystem(session.accessToken).then((s) => alive && setSys(s));
    return () => {
      alive = false;
    };
  }, [session.accessToken]);

  const title = sys
    ? [`Môi trường: ${sys.environment}`, `DB: ${sys.database ?? '-'}`, `SAP: ${sys.sap ?? '-'}`, `Máy chủ: ${sys.machine}`,
        `Phiên bản: ${sys.version ?? '-'}`, `Khởi động: ${formatTime(sys.startedAtUtc)}`,
        sys.configFile ? `File cấu hình: ${sys.configFile}` : '',
        sys.restartOnConfigChange ? 'Sửa cấu hình → tự khởi động lại để áp dụng' : 'Sửa cấu hình cần khởi động lại API'].filter(Boolean).join('\n')
    : '';

  return (
    <header className="topbar">
      <div className="topbar-left">
        <strong>RDM SAP Integration</strong>
        {sys && (
          <span className="env-badge" style={sys.color ? { background: sys.color, borderColor: sys.color } : undefined} title={title}>
            {sys.label}
          </span>
        )}
        <nav className="tabs" aria-label="Chức năng">
          <button className={view === 'endpoints' ? 'tab active' : 'tab'} onClick={() => onNavigate('endpoints')}>
            Quản trị API
          </button>
          <button className={view === 'sapcalls' ? 'tab active' : 'tab'} onClick={() => onNavigate('sapcalls')}>
            Nhật ký SAP
          </button>
          <button className={view === 'dbsync' ? 'tab active' : 'tab'} onClick={() => onNavigate('dbsync')}>
            Đồng bộ DB Dev
          </button>
        </nav>
      </div>
      <div className="topbar-right">
        {sys?.database && <span className="muted small-text db-hint">DB {sys.database}</span>}
        <span className="muted">
          {session.username} ({session.role}) · hết hạn {formatTime(session.expiresAtUtc)}
        </span>
        <button onClick={onLogout}>Đăng xuất</button>
      </div>
    </header>
  );
}
