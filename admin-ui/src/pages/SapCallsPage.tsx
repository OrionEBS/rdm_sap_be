import { useCallback, useEffect, useState } from 'react';
import { api, formatTime, type SapCallDetail, type SapCallFilter, type SapCallPage, type SapCallWriterStats, type Session } from '../api';
import Topbar, { type View } from '../components/Topbar';

interface Props {
  session: Session;
  onNavigate: (view: View) => void;
  onLogout: () => void;
}

const RANGES: { label: string; hours: number }[] = [
  { label: '1 giờ', hours: 1 },
  { label: '24 giờ', hours: 24 },
  { label: '7 ngày', hours: 24 * 7 },
  { label: '30 ngày', hours: 24 * 30 }
];

const STATUS_LABEL: Record<string, string> = { OK: 'OK', WARNING: 'Cảnh báo', ERROR: 'Lỗi SAP', EXCEPTION: 'Lỗi RFC' };

/** Nhật ký gọi SAP: tìm theo X-Correlation-Id / requestId / hàm / trạng thái, xem JSON gửi - nhận để đối soát với EU. */
export default function SapCallsPage({ session, onNavigate, onLogout }: Props) {
  const token = session.accessToken;
  const [hours, setHours] = useState(24);
  const [form, setForm] = useState<SapCallFilter>({});
  const [filter, setFilter] = useState<SapCallFilter>({});
  const [page, setPage] = useState(1);
  const [data, setData] = useState<SapCallPage | null>(null);
  const [stats, setStats] = useState<SapCallWriterStats | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [detail, setDetail] = useState<SapCallDetail | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const byKey = !!(filter.correlationId || filter.requestId);
      const from = byKey ? undefined : new Date(Date.now() - hours * 3600_000).toISOString();
      const [p, s] = await Promise.all([api.sapCalls(token, { ...filter, from, page, pageSize: 50 }), api.sapCallWriter(token)]);
      setData(p);
      setStats(s);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, [token, filter, hours, page]);

  useEffect(() => {
    void load();
  }, [load]);

  function search(e?: React.FormEvent) {
    e?.preventDefault();
    setPage(1);
    setFilter({ ...form });
  }

  async function open(id: number) {
    try {
      setDetail(await api.sapCall(token, id));
    } catch (e) {
      setError((e as Error).message);
    }
  }

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const set = (k: keyof SapCallFilter) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setForm((f) => ({ ...f, [k]: e.target.value }));

  return (
    <div className="page">
      <Topbar session={session} view="sapcalls" onNavigate={onNavigate} onLogout={onLogout} />
      <main>
        <h1 className="page-title">Nhật ký gọi SAP</h1>
        <p className="hint">
          Mỗi lời gọi function module: tham số gửi, kết quả nhận (JSON), thời gian, trạng thái, X-Correlation-Id / requestId của EU. Ghi nền theo lô —
          không làm chậm lời gọi SAP.
        </p>

        {stats && (
          <section className="cards">
            <div className="card">
              <span className="card-value">{stats.written.toLocaleString('vi-VN')}</span>
              <span className="card-label">Đã ghi (từ lúc khởi động)</span>
            </div>
            <div className={`card ${stats.queueLength > 1000 ? 'warn' : ''}`}>
              <span className="card-value">{stats.queueLength.toLocaleString('vi-VN')}</span>
              <span className="card-label">Đang chờ ghi</span>
            </div>
            <div className={`card ${stats.dropped > 0 ? 'off' : 'ok'}`}>
              <span className="card-value">{stats.dropped.toLocaleString('vi-VN')}</span>
              <span className="card-label">Bị bỏ (hàng đợi đầy)</span>
            </div>
            <div className={`card ${stats.fallbackFilesPending > 0 ? 'warn' : 'ok'}`}>
              <span className="card-value">{stats.fallbackFilesPending}</span>
              <span className="card-label">File dự phòng chờ nạp</span>
            </div>
          </section>
        )}
        {stats?.lastError && (
          <div className="alert warn">
            Lỗi ghi DB gần nhất ({formatTime(stats.lastErrorUtc)}): {stats.lastError} — bản ghi đã lưu file dự phòng, tự nạp lại khi DB chạy.
          </div>
        )}

        <form className="toolbar filters" onSubmit={search}>
          <input placeholder="X-Correlation-Id" value={form.correlationId ?? ''} onChange={set('correlationId')} />
          <input placeholder="requestId" value={form.requestId ?? ''} onChange={set('requestId')} />
          <input placeholder="Hàm SAP (VD Z_ESD016_PRICES_CONDITIONS)" value={form.function ?? ''} onChange={set('function')} />
          <select value={form.status ?? ''} onChange={set('status')}>
            <option value="">Mọi trạng thái</option>
            <option value="ERROR">Lỗi SAP (E / A)</option>
            <option value="EXCEPTION">Lỗi RFC (exception)</option>
            <option value="WARNING">Cảnh báo</option>
            <option value="OK">OK</option>
          </select>
          <input placeholder="Thao tác / user / nội dung lỗi" value={form.q ?? ''} onChange={set('q')} />
          <button className="primary" type="submit" disabled={loading}>
            Tìm
          </button>
        </form>
        <section className="toolbar">
          <span className="muted">Khoảng thời gian:</span>
          {RANGES.map((r) => (
            <button key={r.hours} className={hours === r.hours ? 'small primary' : 'small'} onClick={() => { setHours(r.hours); setPage(1); }}>
              {r.label}
            </button>
          ))}
          {(filter.correlationId || filter.requestId) && <span className="muted small-text">(tìm theo mã đối soát: mọi thời điểm)</span>}
          <span className="spacer" />
          <button className="small" onClick={() => void load()} disabled={loading}>
            {loading ? 'Đang tải…' : 'Tải lại'}
          </button>
        </section>

        {error && <div className="alert error">{error}</div>}

        {data && (
          <section className="group">
            <div className="group-head">
              <strong>{data.total.toLocaleString('vi-VN')} lời gọi</strong>
              <span className="spacer" />
              <button className="small" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
                ‹ Trước
              </button>
              <span className="muted small-text">
                Trang {page} / {pages}
              </span>
              <button className="small" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
                Sau ›
              </button>
            </div>
            {data.items.length === 0 ? (
              <p className="muted pad" style={{ padding: '16px' }}>
                Không có lời gọi nào.
              </p>
            ) : (
              <div className="table-scroll">
                <table className="calls">
                  <thead>
                    <tr>
                      <th>Thời điểm</th>
                      <th>Hàm SAP</th>
                      <th>Trạng thái</th>
                      <th className="num">ms</th>
                      <th>Đối soát</th>
                      <th>Nguồn</th>
                      <th>Nội dung</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.items.map((c) => (
                      <tr key={c.id} className={`clickable status-${c.status.toLowerCase()}`} onClick={() => void open(c.id)}>
                        <td className="nowrap">{formatTime(c.startedAtUtc)}</td>
                        <td>
                          <code>{c.functionName}</code>
                          {c.operation && <div className="muted small-text">{c.operation}</div>}
                        </td>
                        <td>
                          <span className={`status-badge s-${c.status.toLowerCase()}`}>{STATUS_LABEL[c.status] ?? c.status}</span>
                        </td>
                        <td className="num">{c.durationMs.toLocaleString('vi-VN')}</td>
                        <td className="small-text">
                          {c.correlationId && <div title="X-Correlation-Id">{c.correlationId}</div>}
                          {c.requestId && <div className="muted" title="requestId">req: {c.requestId}</div>}
                        </td>
                        <td className="small-text">
                          {c.userName ?? c.source}
                          {c.path && <div className="muted">{c.httpMethod} {c.path}</div>}
                        </td>
                        <td className="small-text">
                          {c.errorCode && <code>{c.errorCode}</code>} {c.message}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        )}
      </main>

      {detail && <CallDialog detail={detail} onClose={() => setDetail(null)} />}
    </div>
  );
}

function CallDialog({ detail, onClose }: { detail: SapCallDetail; onClose: () => void }) {
  const c = detail.call;
  const [tab, setTab] = useState<'request' | 'response'>(c.status === 'OK' ? 'request' : 'response');
  const json = JSON.stringify(tab === 'request' ? detail.request : detail.response, null, 2);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  function download() {
    const blob = new Blob([JSON.stringify({ call: c, request: detail.request, response: detail.response }, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `sap-call-${c.id}-${c.functionName}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  const rows: [string, string | number | null][] = [
    ['Thời điểm (UTC)', c.startedAtUtc],
    ['Thời gian', `${c.durationMs} ms`],
    ['Hệ thống SAP', c.sapSystem],
    ['X-Correlation-Id', c.correlationId],
    ['requestId', c.requestId],
    ['Thao tác', c.operation],
    ['Posting', c.postingId],
    ['Nguồn', [c.source, c.userName, c.clientIp].filter(Boolean).join(' · ')],
    ['URL', c.path ? `${c.httpMethod} ${c.path}` : null],
    ['Trace id', c.traceId],
    ['Máy chủ', c.machine],
    ['Call id', c.callId]
  ];

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal wide xl" role="dialog" aria-modal="true" onClick={(e) => e.stopPropagation()}>
        <h2>
          <code>{c.functionName}</code> <span className={`status-badge s-${c.status.toLowerCase()}`}>{STATUS_LABEL[c.status] ?? c.status}</span>
        </h2>
        {c.message && (
          <p className={`alert ${c.status === 'OK' ? 'info' : c.status === 'WARNING' ? 'warn' : 'error'}`}>
            {c.errorCode && <code>{c.errorCode}</code>} {c.message}
          </p>
        )}
        <dl className="kv">
          {rows
            .filter(([, v]) => v !== null && v !== '')
            .map(([k, v]) => (
              <div key={k}>
                <dt>{k}</dt>
                <dd>{v}</dd>
              </div>
            ))}
        </dl>
        <div className="toolbar">
          <button className={tab === 'request' ? 'small primary' : 'small'} onClick={() => setTab('request')}>
            Tham số gửi ({c.requestBytes.toLocaleString('vi-VN')} ký tự)
          </button>
          <button className={tab === 'response' ? 'small primary' : 'small'} onClick={() => setTab('response')}>
            Kết quả nhận ({c.responseBytes.toLocaleString('vi-VN')} ký tự)
          </button>
          {c.truncated && <span className="badge warn">có bảng bị cắt bớt dòng</span>}
          <span className="spacer" />
          <button className="small" onClick={() => void navigator.clipboard?.writeText(json)}>
            Sao chép
          </button>
          <button className="small" onClick={download}>
            Tải JSON
          </button>
        </div>
        <pre className="json">{json}</pre>
        <div className="modal-actions">
          <button onClick={onClose}>Đóng</button>
        </div>
      </div>
    </div>
  );
}
