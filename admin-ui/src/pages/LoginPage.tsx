import { useState, type FormEvent } from 'react';
import { api, ApiError, type Session } from '../api';

interface Props {
  notice: string | null;
  onLogin: (s: Session) => void;
}

export default function LoginPage({ notice, onLogin }: Props) {
  const [username, setUsername] = useState('admin');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const s = await api.login(username.trim(), password);
      if (s.role !== 'Admin') {
        setError(`Tài khoản "${s.username}" có quyền ${s.role} — cần quyền Admin.`);
        return;
      }
      onLogin(s);
    } catch (err) {
      setError(err instanceof ApiError && err.status === 401 ? 'Sai tên đăng nhập hoặc mật khẩu.' : (err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login-wrap">
      <form className="login-card" onSubmit={submit}>
        <h1>RDM SAP Integration</h1>
        <p className="muted">Quản trị API — bật / tắt API</p>
        {notice && <div className="alert info">{notice}</div>}
        {error && <div className="alert error">{error}</div>}
        <label>
          Tên đăng nhập
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" required />
        </label>
        <label>
          Mật khẩu
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required autoFocus />
        </label>
        <button type="submit" className="primary" disabled={busy}>
          {busy ? 'Đang đăng nhập…' : 'Đăng nhập'}
        </button>
      </form>
    </div>
  );
}
