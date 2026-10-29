import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, formatTime, type ApiEndpoint, type ApiEndpointList, type OutputProfile, type Session, type StatusResult } from '../api';
import ConfirmDialog, { type PendingChange } from '../components/ConfirmDialog';
import DocDialog from '../components/DocDialog';
import OutputDialog from '../components/OutputDialog';
import Toggle from '../components/Toggle';
import Topbar, { type View } from '../components/Topbar';

interface Props {
  session: Session;
  onNavigate: (view: View) => void;
  onLogout: () => void;
}

type StatusFilter = 'all' | 'active' | 'inactive' | 'locked' | 'stale' | 'nodoc' | 'hidden' | 'output';

interface Toast {
  kind: 'ok' | 'error' | 'info';
  text: string;
}

export default function EndpointsPage({ session, onNavigate, onLogout }: Props) {
  const [data, setData] = useState<ApiEndpointList | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<StatusFilter>('all');
  const [group, setGroup] = useState('');
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [pending, setPending] = useState<PendingChange | null>(null);
  const [saving, setSaving] = useState(false);
  const [toast, setToast] = useState<Toast | null>(null);
  const [docTarget, setDocTarget] = useState<ApiEndpoint | null>(null);
  const [outputTarget, setOutputTarget] = useState<ApiEndpoint | null>(null);
  const [outputs, setOutputs] = useState<OutputProfile[]>([]);

  const load = useCallback(
    async (reloadCache = false) => {
      setLoading(true);
      setError(null);
      try {
        setData(reloadCache ? await api.refresh(session.accessToken) : await api.list(session.accessToken));
        setOutputs(await api.outputProfiles(session.accessToken).catch(() => []));
      } catch (e) {
        setError((e as Error).message);
      } finally {
        setLoading(false);
      }
    },
    [session.accessToken]
  );

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!toast) return;
    const t = window.setTimeout(() => setToast(null), 6000);
    return () => window.clearTimeout(t);
  }, [toast]);

  /** Cấu hình output theo API: cấu hình gốc + các API sao chép. */
  const outputByKey = useMemo(() => {
    const map = new Map<string, { original: OutputProfile | null; copies: OutputProfile[] }>();
    for (const p of outputs) {
      const entry = map.get(p.endpointKey) ?? { original: null, copies: [] };
      if (p.copyName) entry.copies.push(p);
      else entry.original = p;
      map.set(p.endpointKey, entry);
    }
    return map;
  }, [outputs]);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (data?.items ?? []).filter((e) => {
      if (group && e.group !== group) return false;
      if (status === 'active' && !e.isActive) return false;
      if (status === 'inactive' && e.isActive) return false;
      if (status === 'locked' && !e.isLocked) return false;
      if (status === 'stale' && e.inCode) return false;
      if (status === 'nodoc' && !needsPublicDoc(e)) return false;
      if (status === 'hidden' && !e.publicHidden) return false;
      if (status === 'output' && !outputByKey.has(e.key)) return false;
      if (!q) return true;
      return [e.key, e.route, e.description ?? '', e.publicSummary ?? '', e.publicDescription ?? '', e.group, e.action, e.note ?? ''].some((v) =>
        v.toLowerCase().includes(q)
      );
    });
  }, [data, search, status, group, outputByKey]);

  const byGroup = useMemo(() => {
    const map = new Map<string, ApiEndpoint[]>();
    for (const e of filtered) map.set(e.group, [...(map.get(e.group) ?? []), e]);
    // Nhóm chỉ gồm API khóa (đăng nhập, quản trị) xuống cuối — nhóm nghiệp vụ lên đầu.
    const lockedOnly = (items: ApiEndpoint[]) => items.every((e) => e.isLocked);
    return [...map.entries()].sort(([a, ia], [b, ib]) => Number(lockedOnly(ia)) - Number(lockedOnly(ib)) || a.localeCompare(b));
  }, [filtered]);

  const selectedItems = useMemo(() => (data?.items ?? []).filter((e) => selected.has(e.key)), [data, selected]);

  function toggleSelect(key: string) {
    setSelected((s) => {
      const n = new Set(s);
      if (n.has(key)) n.delete(key);
      else n.add(key);
      return n;
    });
  }

  function selectGroup(items: ApiEndpoint[], on: boolean) {
    setSelected((s) => {
      const n = new Set(s);
      for (const e of items.filter((x) => !x.isLocked)) {
        if (on) n.add(e.key);
        else n.delete(e.key);
      }
      return n;
    });
  }

  async function apply(change: PendingChange, note: string) {
    setSaving(true);
    try {
      const r: StatusResult = await api.setStatus(session.accessToken, {
        keys: change.keys,
        groups: change.groups,
        isActive: change.isActive,
        note: note.trim() || null
      });
      const parts = [`${change.isActive ? 'Đã bật' : 'Đã tắt'} ${r.changed.length} API.`];
      if (r.alreadyInState.length) parts.push(`${r.alreadyInState.length} API đã ở trạng thái này.`);
      if (r.skippedLocked.length) parts.push(`Bỏ qua ${r.skippedLocked.length} API khóa.`);
      if (r.unknownKeys.length || r.unknownGroups.length) parts.push(`Không tìm thấy: ${[...r.unknownKeys, ...r.unknownGroups].join(', ')}.`);
      setToast({ kind: r.changed.length ? 'ok' : 'info', text: parts.join(' ') });
      setPending(null);
      setSelected(new Set());
      await load();
    } catch (e) {
      setToast({ kind: 'error', text: (e as Error).message });
    } finally {
      setSaving(false);
    }
  }

  async function saveDoc(e: ApiEndpoint, doc: { publicSummary: string | null; publicDescription: string | null; publicHidden: boolean }) {
    setSaving(true);
    try {
      await api.setDoc(session.accessToken, { key: e.key, ...doc });
      setToast({ kind: 'ok', text: `Đã lưu mô tả đối tác cho ${e.httpMethod} ${e.route} — Swagger /swagger cập nhật ngay.` });
      setDocTarget(null);
      await load();
    } catch (err) {
      setToast({ kind: 'error', text: (err as Error).message });
    } finally {
      setSaving(false);
    }
  }

  async function download(path: string, name: string) {
    try {
      await api.download(session.accessToken, path, name);
    } catch (err) {
      setToast({ kind: 'error', text: (err as Error).message });
    }
  }

  const lockedCount = data?.items.filter((e) => e.isLocked).length ?? 0;
  const staleCount = data?.items.filter((e) => !e.inCode).length ?? 0;
  const noDocCount = data?.items.filter(needsPublicDoc).length ?? 0;

  return (
    <div className="page">
      <Topbar session={session} view="endpoints" onNavigate={onNavigate} onLogout={onLogout} />

      <main>
        <section className="cards">
          <Card label="Tổng API" value={data?.total} onClick={() => setStatus('all')} active={status === 'all'} />
          <Card label="Đang bật" value={data?.activeCount} tone="ok" onClick={() => setStatus('active')} active={status === 'active'} />
          <Card label="Đang tắt" value={data?.inactiveCount} tone="off" onClick={() => setStatus('inactive')} active={status === 'inactive'} />
          <Card label="Khóa (luôn bật)" value={lockedCount} onClick={() => setStatus('locked')} active={status === 'locked'} />
          <Card label="Chưa có mô tả đối tác" value={noDocCount} tone={noDocCount ? 'warn' : 'ok'} onClick={() => setStatus('nodoc')} active={status === 'nodoc'} />
          {staleCount > 0 && (
            <Card label="Không còn trong code" value={staleCount} tone="warn" onClick={() => setStatus('stale')} active={status === 'stale'} />
          )}
        </section>

        <p className="hint">
          API đang tắt: <b>không hiện trên Swagger</b> và trả <b>404</b> như URL không tồn tại. Thay đổi áp dụng ngay trên API này; các
          instance khác tự cập nhật sau tối đa 30 giây.
          {data?.cacheLoadedAtUtc && <span className="muted"> Cache nạp lúc {formatTime(data.cacheLoadedAtUtc)}.</span>}
        </p>

        <section className="toolbar">
          <input className="search" placeholder="Tìm theo route, mô tả, nhóm, ghi chú…" value={search} onChange={(e) => setSearch(e.target.value)} />
          <select value={group} onChange={(e) => setGroup(e.target.value)}>
            <option value="">Tất cả nhóm</option>
            {data?.groups.map((g) => (
              <option key={g} value={g}>
                {g}
              </option>
            ))}
          </select>
          <select value={status} onChange={(e) => setStatus(e.target.value as StatusFilter)}>
            <option value="all">Mọi trạng thái</option>
            <option value="active">Đang bật</option>
            <option value="inactive">Đang tắt</option>
            <option value="locked">Khóa</option>
            <option value="stale">Không còn trong code</option>
            <option value="nodoc">Chưa có mô tả đối tác</option>
            <option value="hidden">Ẩn khỏi tài liệu đối tác</option>
            <option value="output">Có cấu hình cột output / API sao chép</option>
          </select>
          <button onClick={() => void load()} disabled={loading}>
            Tải lại
          </button>
          <button onClick={() => void load(true)} disabled={loading} title="Nạp lại cache của API từ DB (sau khi sửa trực tiếp trên DB)">
            Nạp lại cache
          </button>
        </section>

        <section className="toolbar docs-bar">
          <span className="muted">Tài liệu:</span>
          <a className="btn-link" href={`${apiBase()}/swagger`} target="_blank" rel="noreferrer" title="Swagger cho đối tác — chỉ mô tả đối tác, không chi tiết SAP">
            Swagger đối tác ↗
          </a>
          <button onClick={() => void download('/api/admin/endpoints/export/ts', 'RDM-API-TS.md')} title="Tài liệu kỹ thuật nội bộ (Markdown) — đầy đủ chi tiết SAP">
            Tải tài liệu TS nội bộ (.md)
          </button>
          <button onClick={() => void download('/api/admin/endpoints/export/openapi-internal', 'RDM-API-openapi-internal.json')} title="OpenAPI nội bộ — Postman / sinh client">
            Tải OpenAPI nội bộ (.json)
          </button>
        </section>

        {selectedItems.length > 0 && (
          <section className="selection-bar">
            <span>Đã chọn {selectedItems.length} API</span>
            <button className="ok" onClick={() => setPending({ title: `${selectedItems.length} API đã chọn`, keys: selectedItems.map((e) => e.key), isActive: true, items: selectedItems })}>
              Bật đã chọn
            </button>
            <button className="danger" onClick={() => setPending({ title: `${selectedItems.length} API đã chọn`, keys: selectedItems.map((e) => e.key), isActive: false, items: selectedItems })}>
              Tắt đã chọn
            </button>
            <button onClick={() => setSelected(new Set())}>Bỏ chọn</button>
          </section>
        )}

        {error && <div className="alert error">{error}</div>}
        {loading && !data && <div className="muted pad">Đang tải…</div>}
        {data && byGroup.length === 0 && <div className="muted pad">Không có API nào khớp bộ lọc.</div>}

        {byGroup.map(([g, items]) => {
          // Nhóm chỉ gồm API khóa: mặc định thu gọn
          const isCollapsed = collapsed[g] ?? items.every((e) => e.isLocked);
          const all = data?.items.filter((e) => e.group === g) ?? [];
          const off = all.filter((e) => !e.isActive).length;
          const editable = items.filter((e) => !e.isLocked);
          const allSelected = editable.length > 0 && editable.every((e) => selected.has(e.key));
          return (
            <section className="group" key={g}>
              <div className="group-head">
                <button className="link" onClick={() => setCollapsed((c) => ({ ...c, [g]: !isCollapsed }))}>
                  {isCollapsed ? '▸' : '▾'} <b>{g}</b>
                </button>
                <span className="muted">
                  {all.length} API · <span className="txt-ok">{all.length - off} bật</span>
                  {off > 0 && (
                    <>
                      {' '}
                      · <span className="txt-off">{off} tắt</span>
                    </>
                  )}
                </span>
                <span className="spacer" />
                {editable.length > 0 && (
                  <>
                    <button className="ok small" onClick={() => setPending({ title: `cả nhóm ${g}`, groups: [g], isActive: true, items: all })}>
                      Bật cả nhóm
                    </button>
                    <button className="danger small" onClick={() => setPending({ title: `cả nhóm ${g}`, groups: [g], isActive: false, items: all })}>
                      Tắt cả nhóm
                    </button>
                  </>
                )}
              </div>
              {!isCollapsed && (
                <table>
                  <thead>
                    <tr>
                      <th className="col-check">
                        <input type="checkbox" checked={allSelected} disabled={editable.length === 0} onChange={(e) => selectGroup(items, e.target.checked)} />
                      </th>
                      <th className="col-method">Method</th>
                      <th>Route / mô tả</th>
                      <th className="col-status">Trạng thái</th>
                      <th className="col-note">Ghi chú / người sửa</th>
                    </tr>
                  </thead>
                  <tbody>
                    {items.map((e) => (
                      <tr key={e.key} className={e.isActive ? '' : 'row-off'}>
                        <td className="col-check">
                          <input type="checkbox" checked={selected.has(e.key)} disabled={e.isLocked} onChange={() => toggleSelect(e.key)} />
                        </td>
                        <td className="col-method">
                          <span className={`method m-${e.httpMethod.toLowerCase()}`}>{e.httpMethod}</span>
                        </td>
                        <td>
                          <code>{e.route}</code>
                          {!e.inCode && <span className="badge warn">không còn trong code</span>}
                          {e.internalOnly ? (
                            <span className="badge muted-badge" title="API quản trị — luôn ẩn khỏi tài liệu đối tác">chỉ nội bộ</span>
                          ) : e.publicHidden ? (
                            <span className="badge muted-badge">ẩn với đối tác</span>
                          ) : (
                            !e.publicSummary && <span className="badge warn">chưa có mô tả đối tác</span>
                          )}
                          {e.publicSummary && (
                            <div className="desc public">
                              <span className="desc-label">Đối tác:</span> {e.publicSummary}
                            </div>
                          )}
                          {e.description && (
                            <div className="desc">
                              <span className="desc-label">Nội bộ:</span> {e.description}
                            </div>
                          )}
                          <button className="link edit-doc" onClick={() => setDocTarget(e)}>
                            ✎ {e.internalOnly ? 'Xem mô tả' : 'Sửa mô tả đối tác'}
                          </button>
                          {!e.internalOnly && e.inCode && (
                            <button className="link edit-doc" onClick={() => setOutputTarget(e)} title="Ẩn / hiện, đổi tên, sắp xếp cột output; reset; sao chép thành API mới">
                              ⚙ Cột output
                            </button>
                          )}
                          {outputByKey.get(e.key)?.original && <span className="badge info-badge">cột output tùy chỉnh</span>}
                          {(outputByKey.get(e.key)?.copies.length ?? 0) > 0 && (
                            <span className="badge info-badge" title={outputByKey.get(e.key)!.copies.map((c) => c.route).join(', ')}>
                              {outputByKey.get(e.key)!.copies.length} API sao chép
                            </span>
                          )}
                        </td>
                        <td className="col-status">
                          {e.isLocked ? (
                            <span className="badge lock" title="API đăng nhập / quản trị luôn bật">🔒 Khóa</span>
                          ) : (
                            <Toggle
                              checked={e.isActive}
                              disabled={saving}
                              label={e.isActive ? 'Bật' : 'Tắt'}
                              onChange={(v) => setPending({ title: `${e.httpMethod} ${e.route}`, keys: [e.key], isActive: v, items: [e] })}
                            />
                          )}
                        </td>
                        <td className="col-note">
                          {e.note && <div>{e.note}</div>}
                          {e.updatedBy && (
                            <div className="muted small-text">
                              {e.updatedBy} · {formatTime(e.updatedAtUtc)}
                            </div>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </section>
          );
        })}
      </main>

      {docTarget && <DocDialog endpoint={docTarget} busy={saving} onCancel={() => setDocTarget(null)} onSave={(doc) => void saveDoc(docTarget, doc)} />}
      {outputTarget && (
        <OutputDialog
          token={session.accessToken}
          endpoint={outputTarget}
          onClose={() => setOutputTarget(null)}
          onChanged={() => void api.outputProfiles(session.accessToken).then(setOutputs).catch(() => undefined)}
          onToast={(kind, text) => setToast({ kind, text })}
        />
      )}
      {pending && <ConfirmDialog change={pending} busy={saving} onCancel={() => setPending(null)} onConfirm={(note) => void apply(pending, note)} />}
      {toast && (
        <div className={`toast ${toast.kind}`} onClick={() => setToast(null)}>
          {toast.text}
        </div>
      )}
    </div>
  );
}

/** Cần soạn mô tả đối tác: chưa có tóm tắt, không bị ẩn khỏi tài liệu đối tác, không phải API quản trị. */
function needsPublicDoc(e: ApiEndpoint) {
  return !e.publicSummary && !e.publicHidden && !e.internalOnly;
}

function apiBase() {
  return (window.RDM_CONFIG?.apiBaseUrl || import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '');
}

function Card({ label, value, tone, onClick, active }: { label: string; value?: number; tone?: 'ok' | 'off' | 'warn'; onClick: () => void; active: boolean }) {
  return (
    <button className={`card ${tone ?? ''} ${active ? 'active' : ''}`} onClick={onClick}>
      <span className="card-value">{value ?? '–'}</span>
      <span className="card-label">{label}</span>
    </button>
  );
}
