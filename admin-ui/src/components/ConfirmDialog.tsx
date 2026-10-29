import { useEffect, useState } from 'react';
import type { ApiEndpoint } from '../api';

export interface PendingChange {
  title: string;
  keys?: string[];
  groups?: string[];
  isActive: boolean;
  /** API sẽ bị ảnh hưởng (để hiển thị trước khi xác nhận). */
  items: ApiEndpoint[];
}

interface Props {
  change: PendingChange;
  busy: boolean;
  onCancel: () => void;
  onConfirm: (note: string) => void;
}

export default function ConfirmDialog({ change, busy, onCancel, onConfirm }: Props) {
  const [note, setNote] = useState('');
  const affected = change.items.filter((e) => !e.isLocked && e.isActive !== change.isActive);
  const locked = change.items.filter((e) => e.isLocked).length;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && !busy && onCancel();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [busy, onCancel]);

  return (
    <div className="modal-backdrop" onClick={() => !busy && onCancel()}>
      <div className="modal" role="dialog" aria-modal="true" onClick={(e) => e.stopPropagation()}>
        <h2>
          {change.isActive ? 'Bật' : 'Tắt'} {change.title}?
        </h2>
        {!change.isActive && (
          <p className="alert warn">API bị tắt sẽ biến khỏi Swagger và trả 404 cho mọi client đang gọi (kể cả hệ thống khác đang tích hợp).</p>
        )}
        <p>
          Sẽ {change.isActive ? 'bật' : 'tắt'} <b>{affected.length}</b> API
          {locked > 0 && <span className="muted"> (bỏ qua {locked} API khóa)</span>}
          {affected.length === 0 && <span className="muted"> — tất cả đã ở trạng thái này</span>}.
        </p>
        {affected.length > 0 && (
          <ul className="affected">
            {affected.slice(0, 12).map((e) => (
              <li key={e.key}>
                <span className={`method m-${e.httpMethod.toLowerCase()}`}>{e.httpMethod}</span> <code>{e.route}</code>
              </li>
            ))}
            {affected.length > 12 && <li className="muted">… và {affected.length - 12} API khác</li>}
          </ul>
        )}
        <label>
          Ghi chú (lý do)
          <textarea value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} rows={2} placeholder="VD: chưa mở cho team test" />
        </label>
        <div className="modal-actions">
          <button onClick={onCancel} disabled={busy}>
            Hủy
          </button>
          <button className={change.isActive ? 'ok' : 'danger'} onClick={() => onConfirm(note)} disabled={busy || affected.length === 0}>
            {busy ? 'Đang lưu…' : change.isActive ? 'Bật' : 'Tắt'}
          </button>
        </div>
      </div>
    </div>
  );
}
