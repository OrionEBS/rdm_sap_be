import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, formatTime, type ApiEndpoint, type OutputDetail, type OutputField, type OutputFieldInfo, type OutputProfile } from '../api';

interface Props {
  token: string;
  endpoint: ApiEndpoint;
  onClose: () => void;
  /** Đã lưu / xóa cấu hình → trang danh sách nạp lại badge. */
  onChanged: () => void;
  onToast: (kind: 'ok' | 'error', text: string) => void;
}

type Tab = 'original' | 'new' | number;
interface Edit {
  visible: boolean;
  alias: string;
}
interface Draft {
  profileId: number | null;
  copyName: string;
  title: string;
  isActive: boolean;
  edits: Record<string, Edit>;
  /** Thứ tự field theo object cha ("" gốc, "items[]" phần tử mảng items). */
  order: Record<string, string[]>;
}

const parentOf = (path: string) => (path.lastIndexOf('.') < 0 ? '' : path.slice(0, path.lastIndexOf('.')));
const stripArray = (key: string) => key.replace(/(\[\])+$/, '');

/** Thứ tự mặc định (theo output gốc của API). */
function defaultOrder(fields: OutputFieldInfo[]): Record<string, string[]> {
  const order: Record<string, string[]> = {};
  for (const f of fields) (order[parentOf(f.path)] ??= []).push(f.path);
  return order;
}

function makeDraft(fields: OutputFieldInfo[], profile: OutputProfile | null): Draft {
  const saved = new Map((profile?.fields ?? []).map((f) => [f.path, f]));
  const edits: Record<string, Edit> = {};
  for (const f of fields) {
    const s = saved.get(f.path);
    edits[f.path] = { visible: s?.visible ?? true, alias: s?.alias ?? '' };
  }
  const order = defaultOrder(fields);
  for (const key of Object.keys(order)) {
    const index = new Map(order[key].map((p, i) => [p, i]));
    order[key] = [...order[key]].sort(
      (a, b) => (saved.get(a)?.order ?? Number.MAX_SAFE_INTEGER) - (saved.get(b)?.order ?? Number.MAX_SAFE_INTEGER) || index.get(a)! - index.get(b)!
    );
  }
  return {
    profileId: profile?.profileId ?? null,
    copyName: profile?.copyName ?? '',
    title: profile?.title ?? '',
    isActive: profile?.isActive ?? true,
    edits,
    order
  };
}

/** Field gửi lên server: chỉ field có thay đổi; object bị đổi thứ tự → gửi order cho mọi field của object đó. */
function toFields(draft: Draft, fields: OutputFieldInfo[]): OutputField[] {
  const base = defaultOrder(fields);
  const ordered = new Map<string, number>();
  for (const [key, list] of Object.entries(draft.order))
    if (list.join('|') !== (base[key] ?? []).join('|')) list.forEach((p, i) => ordered.set(p, i + 1));
  return fields
    .map((f) => {
      const e = draft.edits[f.path];
      return { path: f.path, visible: e.visible, alias: e.alias.trim() || null, order: ordered.get(f.path) ?? null };
    })
    .filter((f) => !f.visible || f.alias || f.order != null);
}

function sample(type: string): unknown {
  if (type.startsWith('string')) return '…';
  if (type === 'number') return 0;
  if (type === 'boolean') return true;
  if (type === 'date') return '2026-10-28';
  if (type === 'datetime') return '2026-10-28T08:00:00Z';
  return null;
}

/** Route API sao chép: /api/c/{tên}/{tham số route API gốc}. */
function copyRoute(name: string, sourceRoute: string) {
  const params = sourceRoute.match(/\{[^}]+\}/g) ?? [];
  return `/api/c/${name || '<tên>'}${params.map((p) => '/{' + p.slice(1, -1).replace(/[:?=*].*$/, '') + '}').join('')}`;
}

function apiOrigin() {
  return (window.RDM_CONFIG?.apiBaseUrl || import.meta.env.VITE_API_BASE_URL || window.location.origin).replace(/\/+$/, '');
}

export default function OutputDialog({ token, endpoint: e, onClose, onChanged, onToast }: Props) {
  const [detail, setDetail] = useState<OutputDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>('original');
  const [draft, setDraft] = useState<Draft | null>(null);
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [search, setSearch] = useState('');
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());

  const byPath = useMemo(() => new Map((detail?.fields ?? []).map((f) => [f.path, f])), [detail]);
  const fields = detail?.fields ?? [];

  const load = useCallback(
    async (select?: Tab) => {
      try {
        const d = await api.outputDetail(token, e.key);
        setDetail(d);
        const t = select ?? 'original';
        setTab(t);
        setDraft(makeDraft(d.fields, t === 'original' ? d.original : d.copies.find((c) => c.profileId === t) ?? null));
        setDirty(false);
      } catch (err) {
        setError((err as Error).message);
      }
    },
    [token, e.key]
  );

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    const onKey = (ev: KeyboardEvent) => ev.key === 'Escape' && !busy && close();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  function close() {
    if (dirty && !window.confirm('Có thay đổi chưa lưu — đóng và bỏ thay đổi?')) return;
    onClose();
  }

  function switchTab(t: Tab) {
    if (!detail || t === tab) return;
    if (dirty && !window.confirm('Có thay đổi chưa lưu — chuyển và bỏ thay đổi?')) return;
    if (t === 'new') {
      // Sao chép: lấy cấu hình đang xem làm điểm bắt đầu.
      setDraft((d) => ({ ...(d ?? makeDraft(detail.fields, null)), profileId: null, copyName: '', title: '', isActive: true }));
      setDirty(true);
    } else {
      setDraft(makeDraft(detail.fields, t === 'original' ? detail.original : detail.copies.find((c) => c.profileId === t) ?? null));
      setDirty(false);
    }
    setTab(t);
  }

  function update(fn: (d: Draft) => Draft) {
    setDraft((d) => (d ? fn(d) : d));
    setDirty(true);
  }

  const setEdit = (path: string, patch: Partial<Edit>) =>
    update((d) => ({ ...d, edits: { ...d.edits, [path]: { ...d.edits[path], ...patch } } }));

  function move(path: string, delta: number) {
    update((d) => {
      const key = parentOf(path);
      const list = [...d.order[key]];
      const i = list.indexOf(path);
      const j = i + delta;
      if (j < 0 || j >= list.length) return d;
      [list[i], list[j]] = [list[j], list[i]];
      return { ...d, order: { ...d.order, [key]: list } };
    });
  }

  function setAllVisible(visible: boolean) {
    update((d) => ({ ...d, edits: Object.fromEntries(Object.entries(d.edits).map(([p, x]) => [p, { ...x, visible }])) }));
  }

  async function save() {
    if (!draft || !detail) return;
    setBusy(true);
    try {
      const saved = await api.saveOutput(token, {
        profileId: draft.profileId,
        endpointKey: detail.endpointKey,
        copyName: tab === 'original' ? null : draft.copyName.trim(),
        title: tab === 'original' ? null : draft.title.trim() || null,
        isActive: draft.isActive,
        fields: toFields(draft, detail.fields)
      });
      onToast('ok', saved.copyName ? `Đã lưu API sao chép ${saved.httpMethod} ${saved.route} — có hiệu lực ngay.` : `Đã lưu cột output của ${e.httpMethod} ${e.route} — có hiệu lực ngay.`);
      onChanged();
      await load(saved.copyName ? saved.profileId : 'original');
    } catch (err) {
      onToast('error', (err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function remove(profile: OutputProfile) {
    const text = profile.copyName
      ? `Xóa API sao chép ${profile.route}? Bên thứ 3 đang gọi URL này sẽ nhận 404.`
      : `Reset ${e.httpMethod} ${e.route} về output ban đầu (hiện mọi cột, tên gốc, thứ tự gốc)?`;
    if (!window.confirm(text)) return;
    setBusy(true);
    try {
      await api.deleteOutput(token, profile.profileId);
      onToast('ok', profile.copyName ? `Đã xóa API sao chép ${profile.route}.` : `Đã reset ${e.route} về output ban đầu.`);
      onChanged();
      await load('original');
    } catch (err) {
      onToast('error', (err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const current: OutputProfile | null =
    tab === 'original' ? detail?.original ?? null : tab === 'new' ? null : detail?.copies.find((c) => c.profileId === tab) ?? null;
  const visibleCount = draft ? fields.filter((f) => draft.edits[f.path]?.visible).length : 0;
  const q = search.trim().toLowerCase();

  function rows(key: string, hiddenByParent: boolean): JSX.Element[] {
    if (!draft) return [];
    const list = draft.order[key] ?? [];
    return list.flatMap((path, i) => {
      const info = byPath.get(path)!;
      const edit = draft.edits[path];
      const off = hiddenByParent || !edit.visible;
      const childKey = info.hasChildren ? Object.keys(draft.order).find((k) => k !== key && stripArray(k) === path) : undefined;
      const match = !q || path.toLowerCase().includes(q) || edit.alias.toLowerCase().includes(q);
      const out: JSX.Element[] = [];
      if (match)
        out.push(
          <tr key={path} className={off ? 'row-off' : ''}>
            <td className="col-check">
              <input type="checkbox" checked={edit.visible} disabled={hiddenByParent || busy} onChange={(ev) => setEdit(path, { visible: ev.target.checked })} />
            </td>
            <td>
              <span style={{ paddingLeft: info.depth * 18 }} className="field-name">
                {childKey && !q ? (
                  <button className="link" onClick={() => setCollapsed((c) => { const n = new Set(c); if (n.has(path)) n.delete(path); else n.add(path); return n; })}>
                    {collapsed.has(path) ? '▸' : '▾'}
                  </button>
                ) : (
                  <span className="tree-pad" />
                )}
                <code>{info.name}</code>
              </span>
              <div className="muted small-text" style={{ paddingLeft: info.depth * 18 + 18 }}>
                {path} · {info.type}
              </div>
            </td>
            <td>
              <input
                className="alias"
                value={edit.alias}
                disabled={off || busy}
                placeholder={info.name}
                maxLength={100}
                onChange={(ev) => setEdit(path, { alias: ev.target.value })}
              />
            </td>
            <td className="col-order">
              <button className="small" title="Lên" disabled={i === 0 || busy || !!q} onClick={() => move(path, -1)}>↑</button>
              <button className="small" title="Xuống" disabled={i === list.length - 1 || busy || !!q} onClick={() => move(path, 1)}>↓</button>
            </td>
          </tr>
        );
      if (childKey && (q || !collapsed.has(path))) out.push(...rows(childKey, off));
      return out;
    });
  }

  /** Cấu trúc output sau cấu hình (giá trị minh họa theo kiểu). */
  function preview(key: string): unknown {
    if (!draft) return null;
    const obj: Record<string, unknown> = {};
    for (const path of draft.order[key] ?? []) {
      const info = byPath.get(path)!;
      const edit = draft.edits[path];
      if (!edit.visible) continue;
      const childKey = info.hasChildren ? Object.keys(draft.order).find((k) => k !== key && stripArray(k) === path) : undefined;
      let value: unknown = sample(info.type);
      if (childKey) {
        value = preview(childKey);
        for (let n = (childKey.match(/\[\]/g) ?? []).length - (path.match(/\[\]/g) ?? []).length; n > 0; n--) value = [value];
      } else if (info.type === 'array') value = [];
      obj[edit.alias.trim() || info.name] = value;
    }
    return obj;
  }

  const rootKey = draft && draft.order['[]'] ? '[]' : '';
  const previewJson = draft ? JSON.stringify(rootKey === '[]' ? [preview('[]')] : preview(''), null, 2) : '';
  const routeShown = tab === 'original' ? e.route : copyRoute(draft?.copyName.trim().toLowerCase() ?? '', e.route);

  return (
    <div className="modal-backdrop" onClick={() => !busy && close()}>
      <div className="modal xl output-dialog" role="dialog" aria-modal="true" onClick={(ev) => ev.stopPropagation()}>
        <h2>
          Cột output — <span className={`method m-${e.httpMethod.toLowerCase()}`}>{e.httpMethod}</span> <code>{e.route}</code>
        </h2>
        {error && <div className="alert error">{error}</div>}
        {!detail && !error && <div className="muted">Đang tải…</div>}

        {detail && detail.fields.length === 0 && (
          <p className="alert warn">API này không khai báo kiểu response (hoặc trả file) — không cấu hình cột output được.</p>
        )}

        {detail && detail.fields.length > 0 && draft && (
          <>
            <div className="tabs">
              <button className={tab === 'original' ? 'active' : ''} onClick={() => switchTab('original')}>
                API gốc {detail.original && <span className="badge info-badge">đã tùy chỉnh</span>}
              </button>
              {detail.copies.map((c) => (
                <button key={c.profileId} className={tab === c.profileId ? 'active' : ''} onClick={() => switchTab(c.profileId)} title={c.route}>
                  {c.copyName} {!c.isActive && <span className="badge muted-badge">tắt</span>}
                </button>
              ))}
              {detail.canCopy && (
                <button className={tab === 'new' ? 'active' : ''} onClick={() => switchTab('new')} title="Tạo API mới cùng xử lý, output theo cấu hình riêng">
                  ＋ Sao chép thành API mới
                </button>
              )}
            </div>

            {tab === 'original' ? (
              <p className="hint">
                Áp dụng cho chính <code>{e.route}</code> — mọi bên đang gọi API này nhận output mới ngay (Swagger cập nhật theo).
                {detail.responseType && <span className="muted"> Kiểu response: {detail.responseType}.</span>}
              </p>
            ) : (
              <div className="copy-form">
                <label>
                  Tên API sao chép <span className="muted">(chữ thường, số, '-')</span>
                  <input
                    value={draft.copyName}
                    disabled={busy}
                    maxLength={60}
                    placeholder="vd: soldto-ngan-gon"
                    onChange={(ev) => update((d) => ({ ...d, copyName: ev.target.value.toLowerCase().replace(/[^a-z0-9-]/g, '') }))}
                  />
                </label>
                <label>
                  Tiêu đề trên Swagger
                  <input value={draft.title} disabled={busy} maxLength={200} placeholder="vd: Danh sách khách hàng (rút gọn cho đối tác X)" onChange={(ev) => update((d) => ({ ...d, title: ev.target.value }))} />
                </label>
                <label className="check">
                  <input type="checkbox" checked={draft.isActive} disabled={busy} onChange={(ev) => update((d) => ({ ...d, isActive: ev.target.checked }))} />
                  Đang bật (tắt = URL trả 404, ẩn khỏi Swagger)
                </label>
                <div className="route-line">
                  <span className={`method m-${e.httpMethod.toLowerCase()}`}>{e.httpMethod}</span> <code>{apiOrigin()}{routeShown}</code>
                  {tab !== 'new' && (
                    <button className="small" onClick={() => void navigator.clipboard?.writeText(apiOrigin() + routeShown).then(() => onToast('ok', 'Đã chép URL.'))}>
                      Chép URL
                    </button>
                  )}
                </div>
                <p className="muted small-text">
                  Cùng request, tham số, quyền và xử lý như <code>{e.route}</code>; chỉ output khác. API gốc đang tắt thì bản sao cũng trả 404.
                  {current && !current.sourceIsActive && <b className="txt-off"> API gốc đang TẮT.</b>}
                </p>
              </div>
            )}

            <div className="toolbar output-toolbar">
              <input className="search" placeholder="Tìm cột…" value={search} onChange={(ev) => setSearch(ev.target.value)} />
              <span className="muted">
                Hiện {visibleCount}/{fields.length} cột
              </span>
              <span className="spacer" />
              <button className="small" disabled={busy} onClick={() => setAllVisible(true)}>Hiện tất cả</button>
              <button className="small" disabled={busy} onClick={() => setAllVisible(false)}>Ẩn tất cả</button>
              <button className="small" disabled={busy} onClick={() => update(() => ({ ...makeDraft(fields, null), profileId: draft.profileId, copyName: draft.copyName, title: draft.title, isActive: draft.isActive }))} title="Đưa bảng về output gốc (chưa lưu)">
                Về output gốc
              </button>
            </div>

            <div className="output-body">
              <div className="output-table">
                <table>
                  <thead>
                    <tr>
                      <th className="col-check">Hiện</th>
                      <th>Cột gốc</th>
                      <th>Tên mới (đổi tên)</th>
                      <th className="col-order">Thứ tự</th>
                    </tr>
                  </thead>
                  <tbody>{rows(rootKey, false)}</tbody>
                </table>
              </div>
              <div className="output-preview">
                <div className="box-label">Output sau cấu hình (minh họa)</div>
                <pre>{previewJson}</pre>
              </div>
            </div>

            {current?.updatedBy && (
              <p className="muted small-text">
                Sửa lần cuối: {current.updatedBy} · {formatTime(current.updatedAtUtc)}
              </p>
            )}
          </>
        )}

        <div className="modal-actions">
          {current && (
            <button className="danger" disabled={busy} onClick={() => void remove(current)} title={current.copyName ? 'Xóa API sao chép' : 'Xóa cấu hình — API trả output như ban đầu'}>
              {current.copyName ? 'Xóa API sao chép' : 'Reset như ban đầu'}
            </button>
          )}
          <span className="spacer" />
          <button onClick={close} disabled={busy}>
            Đóng
          </button>
          {detail && detail.fields.length > 0 && (
            <button className="primary" disabled={busy || !dirty || (tab !== 'original' && draft?.copyName.trim().length! < 2)} onClick={() => void save()}>
              {busy ? 'Đang lưu…' : tab === 'new' ? 'Tạo API sao chép' : 'Lưu'}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
