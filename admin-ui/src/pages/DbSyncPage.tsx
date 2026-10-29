import { useCallback, useEffect, useState } from 'react';
import { api, formatTime, type DbCompareRow, type DbImportMode, type DbSyncRunResult, type DbSyncStatus, type Session } from '../api';
import Topbar, { type View } from '../components/Topbar';

interface Props {
  session: Session;
  onNavigate: (view: View) => void;
  onLogout: () => void;
}

const ACTION_LABEL: Record<string, string> = {
  replace: 'Nạp toàn bộ',
  merge: 'Gộp',
  'skip-target-has-data': 'Bỏ qua — đích đã có dữ liệu',
  'skip-source-empty': 'Bỏ qua — nguồn trống',
  'skip-not-in-snapshot': 'Bỏ qua'
};

/** Nút "Đồng bộ sang DB Dev": DB của API này → DbSync:TargetConnectionString, ghi qua stored procedure. */
export default function DbSyncPage({ session, onNavigate, onLogout }: Props) {
  const token = session.accessToken;
  const [status, setStatus] = useState<DbSyncStatus | null>(null);
  const [compare, setCompare] = useState<DbCompareRow[] | null>(null);
  const [compareError, setCompareError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [mode, setMode] = useState<DbImportMode>('IfEmpty');
  const [migrate, setMigrate] = useState(false);
  const [busy, setBusy] = useState<'preview' | 'run' | null>(null);
  const [preview, setPreview] = useState<DbSyncRunResult | null>(null);
  const [result, setResult] = useState<DbSyncRunResult | null>(null);
  const [confirming, setConfirming] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const s = await api.dbSyncStatus(token);
      setStatus(s);
      if (s.enabled) {
        setCompareError(null);
        try {
          setCompare(await api.dbSyncCompare(token));
        } catch (e) {
          setCompare(null);
          setCompareError((e as Error).message);
        }
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, [token]);

  useEffect(() => {
    void load();
  }, [load]);

  // Đổi chế độ / tùy chọn → bản xem trước cũ không còn đúng.
  useEffect(() => setPreview(null), [mode, migrate]);

  async function runPreview() {
    setBusy('preview');
    setResult(null);
    try {
      setPreview(await api.dbSyncRun(token, { mode, dryRun: true, migrate }));
    } catch (e) {
      setPreview(null);
      setError((e as Error).message);
    } finally {
      setBusy(null);
    }
  }

  async function runSync(confirmTarget: string | null) {
    setBusy('run');
    setError(null);
    try {
      const r = await api.dbSyncRun(token, { mode, dryRun: false, migrate, confirmTarget });
      setResult(r);
      setPreview(null);
      setConfirming(false);
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(null);
    }
  }

  const targetMissing = compare?.every((r) => r.targetRows === null) ?? false;
  const differs = compare?.filter((r) => r.sourceRows !== r.targetRows).length ?? 0;

  return (
    <div className="page">
      <Topbar session={session} view="dbsync" onNavigate={onNavigate} onLogout={onLogout} />
      <main>
        <h1 className="page-title">Đồng bộ dữ liệu sang DB Dev</h1>
        <p className="hint">
          Chép <b>master data</b> (khách hàng, ship-to, vật tư, tài khoản, cost center, danh mục SD…) và <b>cấu hình API</b> (bật / tắt, mô tả
          đối tác) từ DB của API này sang DB Dev — bên Dev có dữ liệu ngay, không cần đồng bộ lại từ SAP. Không chép chứng từ, job, log.
        </p>

        {error && <div className="alert error">{error}</div>}
        {loading && !status && <p className="muted pad">Đang tải…</p>}

        {status && !status.enabled && (
          <div className="alert warn">
            Chưa cấu hình DB đích: khai báo <code>DbSync:TargetConnectionString</code> trong cấu hình của RDM API rồi khởi động lại API.
          </div>
        )}

        {status?.enabled && (
          <>
            <section className="sync-route">
              <div className="db-box">
                <div className="box-label">Nguồn (DB của API này)</div>
                <code>{status.source}</code>
              </div>
              <div className="sync-arrow" aria-hidden="true">
                →
              </div>
              <div className="db-box target">
                <div className="box-label">Đích (DB Dev)</div>
                <code>{status.target}</code>
              </div>
            </section>

            <section className="group">
              <div className="group-head">
                <strong>So sánh số dòng</strong>
                {compare && (
                  <span className={differs ? 'badge warn' : 'badge muted-badge'}>{differs ? `${differs} bảng lệch` : 'khớp hoàn toàn'}</span>
                )}
                <span className="spacer" />
                <button className="small" onClick={() => void load()} disabled={loading || !!busy}>
                  {loading ? 'Đang tải…' : 'Tải lại'}
                </button>
              </div>
              {compareError && <div className="alert error">Không đọc được DB: {compareError}</div>}
              {targetMissing && (
                <div className="alert warn">DB đích chưa có bảng RDM — lần đầu cần tick “Cho phép tạo / cập nhật schema”.</div>
              )}
              {compare && (
                <table>
                  <thead>
                    <tr>
                      <th>Bảng</th>
                      <th className="num">Nguồn</th>
                      <th className="num">Đích</th>
                    </tr>
                  </thead>
                  <tbody>
                    {compare.map((r) => (
                      <tr key={r.table} className={r.sourceRows !== r.targetRows ? 'row-diff' : undefined}>
                        <td>
                          <code>{r.table}</code>
                        </td>
                        <td className="num">{fmt(r.sourceRows)}</td>
                        <td className="num">{r.targetRows === null ? <span className="muted">chưa có</span> : fmt(r.targetRows)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </section>

            <section className="group sync-options">
              <div className="group-head">
                <strong>Đồng bộ</strong>
              </div>
              <div className="options-body">
                <label className="radio">
                  <input type="radio" name="mode" checked={mode === 'IfEmpty'} onChange={() => setMode('IfEmpty')} />
                  <span>
                    <b>Chỉ bảng còn trống</b> (an toàn) — bảng đích đã có dữ liệu giữ nguyên; API đã chỉnh bên Dev không bị đè.
                  </span>
                </label>
                <label className="radio">
                  <input type="radio" name="mode" checked={mode === 'Replace'} onChange={() => setMode('Replace')} />
                  <span>
                    <b>Thay toàn bộ</b> — xóa master data bên Dev và nạp lại theo nguồn; chép đè cấu hình API.
                  </span>
                </label>
                {status.allowMigrate && (
                  <label className="check">
                    <input type="checkbox" checked={migrate} onChange={(e) => setMigrate(e.target.checked)} />
                    <span>Cho phép tạo / cập nhật schema DB đích (EF migration) — cần khi DB Dev mới hoặc chạy bản cũ hơn.</span>
                  </label>
                )}
                <div className="toolbar">
                  <button onClick={() => void runPreview()} disabled={!!busy || status.running}>
                    {busy === 'preview' ? 'Đang xem trước…' : '1. Xem trước'}
                  </button>
                  <button
                    className={mode === 'Replace' ? 'danger' : 'primary'}
                    onClick={() => (mode === 'Replace' ? setConfirming(true) : void runSync(null))}
                    disabled={!!busy || status.running}
                  >
                    {busy === 'run' ? 'Đang đồng bộ…' : '2. Đồng bộ sang DB Dev'}
                  </button>
                  {status.running && !busy && <span className="muted">Đang có lần đồng bộ khác chạy…</span>}
                </div>
              </div>
              {preview && <RunTable title="Xem trước (chưa ghi gì)" run={preview} />}
              {result && <RunTable title={result.success ? 'Kết quả đồng bộ' : 'Đồng bộ lỗi'} run={result} />}
            </section>

            {status.lastRun && status.lastRun.startedAtUtc !== result?.startedAtUtc && (
              <p className="muted small-text">
                Lần đồng bộ gần nhất: {formatTime(status.lastRun.startedAtUtc)} bởi {status.lastRun.requestedBy} ·{' '}
                {status.lastRun.mode === 'Replace' ? 'thay toàn bộ' : 'chỉ bảng trống'} ·{' '}
                {status.lastRun.success ? <span className="txt-ok">thành công</span> : <span className="txt-off">lỗi: {status.lastRun.error}</span>}
              </p>
            )}
          </>
        )}
      </main>

      {confirming && status?.targetDatabase && (
        <ReplaceConfirm database={status.targetDatabase} target={status.target ?? ''} busy={busy === 'run'} onCancel={() => setConfirming(false)}
          onConfirm={(name) => void runSync(name)} />
      )}
    </div>
  );
}

function RunTable({ title, run }: { title: string; run: DbSyncRunResult }) {
  const written = run.tables.filter((t) => t.action === 'replace' || t.action === 'merge').length;
  return (
    <div className="run-result">
      <div className={`alert ${run.success ? (run.dryRun ? 'info' : 'info') : 'error'}`}>
        <b>{title}</b> — {run.success ? `${run.dryRun ? 'sẽ nạp' : 'đã nạp'} ${written} bảng, bỏ qua ${run.tables.length - written} bảng` : run.error} ·{' '}
        {(run.durationMs / 1000).toFixed(1)} giây
      </div>
      {run.tables.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Bảng</th>
              <th>Việc</th>
              <th className="num">Nguồn</th>
              <th className="num">Đích trước</th>
              <th className="num">Đích sau</th>
              <th>Chi tiết</th>
            </tr>
          </thead>
          <tbody>
            {run.tables.map((t) => (
              <tr key={t.table} className={t.action.startsWith('skip') ? 'row-skip' : undefined}>
                <td>
                  <code>{t.table}</code>
                </td>
                <td>{ACTION_LABEL[t.action] ?? t.action}</td>
                <td className="num">{fmt(t.sourceRows)}</td>
                <td className="num">{fmt(t.targetRowsBefore)}</td>
                <td className="num">{run.dryRun ? '' : fmt(t.targetRowsAfter)}</td>
                <td className="muted small-text">{t.detail}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function ReplaceConfirm(props: { database: string; target: string; busy: boolean; onCancel: () => void; onConfirm: (name: string) => void }) {
  const [name, setName] = useState('');
  const ok = name.trim().toLowerCase() === props.database.toLowerCase();
  return (
    <div className="modal-backdrop" onClick={() => !props.busy && props.onCancel()}>
      <div className="modal" role="dialog" aria-modal="true" onClick={(e) => e.stopPropagation()}>
        <h2>Thay toàn bộ dữ liệu DB Dev?</h2>
        <p className="alert warn">
          Master data trong <code>{props.target}</code> sẽ bị xóa và nạp lại theo nguồn; cấu hình API bên Dev (bật / tắt, mô tả) bị chép đè.
        </p>
        <label>
          <span>
            Gõ tên database đích <code>{props.database}</code> để xác nhận
          </span>
          <input value={name} onChange={(e) => setName(e.target.value)} autoFocus placeholder={props.database} />
        </label>
        <div className="modal-actions">
          <button onClick={props.onCancel} disabled={props.busy}>
            Hủy
          </button>
          <button className="danger" disabled={!ok || props.busy} onClick={() => props.onConfirm(name.trim())}>
            {props.busy ? 'Đang đồng bộ…' : 'Thay toàn bộ'}
          </button>
        </div>
      </div>
    </div>
  );
}

const fmt = (n: number | null | undefined) => (n === null || n === undefined ? '–' : n.toLocaleString('vi-VN'));
