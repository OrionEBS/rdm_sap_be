import { useEffect, useState } from 'react';
import { findSapTerms, type ApiEndpoint } from '../api';

interface Props {
  endpoint: ApiEndpoint;
  busy: boolean;
  onCancel: () => void;
  onSave: (doc: { publicSummary: string | null; publicDescription: string | null; publicHidden: boolean }) => void;
}

/** Soạn mô tả cho ĐỐI TÁC (Swagger /swagger); mô tả NỘI BỘ (từ code) hiện để tham khảo, không sửa ở đây. */
export default function DocDialog({ endpoint: e, busy, onCancel, onSave }: Props) {
  const [summary, setSummary] = useState(e.publicSummary ?? '');
  const [description, setDescription] = useState(e.publicDescription ?? '');
  const [hidden, setHidden] = useState(e.publicHidden && !e.internalOnly);
  const sapTerms = findSapTerms(summary, description);

  useEffect(() => {
    const onKey = (ev: KeyboardEvent) => ev.key === 'Escape' && !busy && onCancel();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [busy, onCancel]);

  return (
    <div className="modal-backdrop" onClick={() => !busy && onCancel()}>
      <div className="modal wide" role="dialog" aria-modal="true" onClick={(ev) => ev.stopPropagation()}>
        <h2>
          Mô tả API — <span className={`method m-${e.httpMethod.toLowerCase()}`}>{e.httpMethod}</span> <code>{e.route}</code>
        </h2>

        <div className="internal-box">
          <div className="box-label">Mô tả NỘI BỘ — từ code, chỉ team nội bộ thấy (Swagger nội bộ, tài liệu TS)</div>
          <div>{e.description ?? <span className="muted">(chưa có XML doc)</span>}</div>
        </div>

        {e.internalOnly ? (
          <p className="alert info">API quản trị — luôn ẩn khỏi tài liệu đối tác (theo code), không cần mô tả đối tác.</p>
        ) : (
          <>
            <label>
              Tóm tắt cho ĐỐI TÁC <span className="muted">({summary.trim().length}/200) — hiện làm tiêu đề API trên Swagger /swagger</span>
              <input value={summary} maxLength={200} onChange={(ev) => setSummary(ev.target.value)} placeholder="VD: Khai báo bảng giá hàng loạt (xử lý nền)" />
            </label>
            <label>
              Mô tả chi tiết cho ĐỐI TÁC <span className="muted">({description.trim().length}/2000, markdown)</span>
              <textarea
                value={description}
                maxLength={2000}
                rows={6}
                onChange={(ev) => setDescription(ev.target.value)}
                placeholder="Cách dùng, ràng buộc nghiệp vụ, mã lỗi trả về… (không ghi tên hàm / bảng / BAPI SAP)"
              />
            </label>
            {sapTerms.length > 0 && (
              <p className="alert warn">
                Mô tả đối tác đang có thuật ngữ kỹ thuật SAP: <b>{sapTerms.join(', ')}</b> — nên viết lại bằng ngôn ngữ nghiệp vụ.
              </p>
            )}
            {!summary.trim() && !hidden && (
              <p className="muted">Để trống: Swagger đối tác chỉ hiện route, không hiện mô tả (không tự lấy mô tả nội bộ).</p>
            )}
            <label className="check">
              <input type="checkbox" checked={hidden} onChange={(ev) => setHidden(ev.target.checked)} />
              Ẩn API này khỏi tài liệu đối tác (vẫn gọi được nếu đang bật — VD API chỉ dùng nội bộ)
            </label>
          </>
        )}

        {e.docUpdatedBy && (
          <p className="muted small-text">
            Sửa mô tả lần cuối: {e.docUpdatedBy} · {new Date(e.docUpdatedAtUtc!.endsWith('Z') ? e.docUpdatedAtUtc! : `${e.docUpdatedAtUtc}Z`).toLocaleString('vi-VN')}
          </p>
        )}
        <div className="modal-actions">
          <button onClick={onCancel} disabled={busy}>
            {e.internalOnly ? 'Đóng' : 'Hủy'}
          </button>
          {!e.internalOnly && (
            <button
              className="primary"
              disabled={busy}
              onClick={() => onSave({ publicSummary: summary.trim() || null, publicDescription: description.trim() || null, publicHidden: hidden })}
            >
              {busy ? 'Đang lưu…' : 'Lưu'}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
