// Gọi RDM API: JWT trong sessionStorage (đóng tab = đăng xuất), lỗi ProblemDetails → thông báo tiếng Việt.

declare global {
  interface Window {
    RDM_CONFIG?: { apiBaseUrl?: string };
  }
}

// Ưu tiên config.js (sửa được sau khi deploy), sau đó VITE_API_BASE_URL lúc build; trống = cùng origin.
const BASE = (window.RDM_CONFIG?.apiBaseUrl || import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '');
const TOKEN_KEY = 'rdm.admin.token';

export interface Session {
  accessToken: string;
  username: string;
  role: string;
  expiresAtUtc: string;
}

export interface ApiEndpoint {
  key: string;
  httpMethod: string;
  route: string;
  group: string;
  action: string;
  description: string | null;
  isActive: boolean;
  isLocked: boolean;
  inCode: boolean;
  note: string | null;
  updatedBy: string | null;
  updatedAtUtc: string | null;
  /** Mô tả ĐỐI TÁC (Swagger /swagger). `description` ở trên là mô tả NỘI BỘ (kỹ thuật, từ code). */
  publicSummary: string | null;
  publicDescription: string | null;
  /** Không hiện trong tài liệu đối tác (do admin chọn, hoặc internalOnly). */
  publicHidden: boolean;
  /** API quản trị — luôn ẩn khỏi tài liệu đối tác (theo code). */
  internalOnly: boolean;
  docUpdatedBy: string | null;
  docUpdatedAtUtc: string | null;
}

export interface ApiEndpointList {
  total: number;
  activeCount: number;
  inactiveCount: number;
  groups: string[];
  cacheLoadedAtUtc: string | null;
  items: ApiEndpoint[];
}

export interface ApiEndpointGroup {
  group: string;
  total: number;
  activeCount: number;
  inactiveCount: number;
  lockedCount: number;
  lastUpdatedAtUtc: string | null;
}

export interface StatusResult {
  isActive: boolean;
  changed: string[];
  skippedLocked: string[];
  alreadyInState: string[];
  unknownKeys: string[];
  unknownGroups: string[];
}

export type DbImportMode = 'IfEmpty' | 'Replace';

export interface DbTableResult {
  table: string;
  /** replace / merge / skip-target-has-data / skip-source-empty / skip-not-in-snapshot */
  action: string;
  sourceRows: number;
  targetRowsBefore: number | null;
  targetRowsAfter: number | null;
  detail: string | null;
}

export interface DbSyncRunResult {
  dryRun: boolean;
  mode: DbImportMode;
  migrate: boolean;
  source: string;
  target: string;
  requestedBy: string;
  startedAtUtc: string;
  durationMs: number;
  success: boolean;
  error: string | null;
  tables: DbTableResult[];
  log: string[];
}

export interface DbSyncStatus {
  enabled: boolean;
  source: string | null;
  target: string | null;
  targetDatabase: string | null;
  tables: string[];
  allowMigrate: boolean;
  running: boolean;
  lastRun: DbSyncRunResult | null;
}

export interface DbCompareRow {
  table: string;
  sourceRows: number | null;
  targetRows: number | null;
}

export interface SapCallRow {
  id: number;
  callId: string;
  startedAtUtc: string;
  durationMs: number;
  functionName: string;
  status: 'OK' | 'WARNING' | 'ERROR' | 'EXCEPTION';
  errorCode: string | null;
  message: string | null;
  correlationId: string | null;
  requestId: string | null;
  operation: string | null;
  postingId: string | null;
  source: string | null;
  userName: string | null;
  clientIp: string | null;
  httpMethod: string | null;
  path: string | null;
  traceId: string | null;
  jobId: string | null;
  machine: string | null;
  sapSystem: string | null;
  requestBytes: number;
  responseBytes: number;
  truncated: boolean;
}

export interface SapCallPage {
  total: number;
  page: number;
  pageSize: number;
  items: SapCallRow[];
}

export interface SapCallDetail {
  call: SapCallRow;
  request: unknown;
  response: unknown;
}

export interface SapCallWriterStats {
  enabled: boolean;
  enqueued: number;
  written: number;
  dropped: number;
  failedBatches: number;
  fallbackWritten: number;
  queueLength: number;
  fallbackFilesPending: number;
  lastFlushUtc: string | null;
  lastError: string | null;
  lastErrorUtc: string | null;
}

export interface SapCallFilter {
  from?: string;
  to?: string;
  function?: string;
  status?: string;
  correlationId?: string;
  requestId?: string;
  operation?: string;
  user?: string;
  q?: string;
  page?: number;
  pageSize?: number;
}

export function sapCallQuery(f: SapCallFilter): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(f)) if (v !== undefined && v !== null && String(v).trim() !== '') p.set(k, String(v).trim());
  const s = p.toString();
  return s ? `?${s}` : '';
}

export interface SystemInfo {
  environment: string;
  label: string;
  color: string | null;
  machine: string;
  version: string | null;
  startedAtUtc: string;
  database: string | null;
  sap: string | null;
  configFile: string | null;
  hangfireServer: boolean;
  restartOnConfigChange: boolean;
  watchedConfigFiles: string[];
}

/** 1 field output: path theo tên JSON gốc ("items[].name1"), visible = false ẩn, alias = tên mới, order = thứ tự trong object. */
export interface OutputField {
  path: string;
  visible: boolean;
  alias?: string | null;
  order?: number | null;
}

export interface OutputFieldInfo {
  path: string;
  name: string;
  type: string;
  depth: number;
  hasChildren: boolean;
}

export interface OutputProfile {
  profileId: number;
  endpointKey: string;
  httpMethod: string;
  sourceRoute: string;
  /** null = cấu hình của chính API gốc; có giá trị = API sao chép /api/c/{copyName}/… */
  copyName: string | null;
  route: string;
  title: string | null;
  isActive: boolean;
  sourceIsActive: boolean;
  fields: OutputField[];
  updatedBy: string | null;
  updatedAtUtc: string | null;
  createdAtUtc: string;
}

export interface OutputDetail {
  endpointKey: string;
  responseType: string | null;
  canCopy: boolean;
  fields: OutputFieldInfo[];
  original: OutputProfile | null;
  copies: OutputProfile[];
}

export interface OutputSaveRequest {
  profileId?: number | null;
  endpointKey: string;
  copyName?: string | null;
  title?: string | null;
  isActive: boolean;
  fields: OutputField[];
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

export function loadSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(TOKEN_KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as Session;
    return new Date(s.expiresAtUtc).getTime() > Date.now() + 30_000 ? s : null;
  } catch {
    return null;
  }
}

export function saveSession(s: Session | null) {
  try {
    if (s) sessionStorage.setItem(TOKEN_KEY, JSON.stringify(s));
    else sessionStorage.removeItem(TOKEN_KEY);
  } catch {
    /* trình duyệt chặn storage → chỉ giữ trong bộ nhớ */
  }
}

/** Được gọi khi token hết hạn / bị từ chối (401) để App quay về màn hình đăng nhập. */
let onUnauthorized: (() => void) | null = null;
export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler;
}

async function request<T>(method: string, path: string, body?: unknown, token?: string): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${BASE}${path}`, {
      method,
      headers: {
        Accept: 'application/json',
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        ...(token ? { Authorization: `Bearer ${token}` } : {})
      },
      body: body !== undefined ? JSON.stringify(body) : undefined
    });
  } catch {
    throw new ApiError(0, `Không kết nối được RDM API${BASE ? ` (${BASE})` : ''} — kiểm tra API đang chạy, địa chỉ và CORS.`);
  }

  const text = await res.text();
  const data = text ? safeJson(text) : null;
  if (res.ok) return data as T;

  if (res.status === 401 && token) {
    onUnauthorized?.();
    throw new ApiError(401, 'Phiên đăng nhập hết hạn — đăng nhập lại.');
  }
  if (res.status === 403) throw new ApiError(403, 'Tài khoản không có quyền Admin.');
  throw new ApiError(res.status, problemMessage(data) ?? `Lỗi HTTP ${res.status}.`);
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function problemMessage(data: unknown): string | null {
  if (!data || typeof data !== 'object') return typeof data === 'string' && data ? data : null;
  const p = data as { detail?: string; title?: string; errors?: Record<string, string[]> };
  if (p.errors) return Object.values(p.errors).flat().join(' ');
  return p.detail ?? p.title ?? null;
}

export const api = {
  login: (username: string, password: string) =>
    request<Session>('POST', '/api/auth/token', { username, password }),
  list: (token: string) => request<ApiEndpointList>('GET', '/api/admin/endpoints', undefined, token),
  groups: (token: string) => request<ApiEndpointGroup[]>('GET', '/api/admin/endpoints/groups', undefined, token),
  setStatus: (token: string, body: { keys?: string[]; groups?: string[]; isActive: boolean; note?: string | null }) =>
    request<StatusResult>('PUT', '/api/admin/endpoints/status', body, token),
  refresh: (token: string) => request<ApiEndpointList>('POST', '/api/admin/endpoints/refresh', undefined, token),
  setDoc: (token: string, body: { key: string; publicSummary: string | null; publicDescription: string | null; publicHidden: boolean }) =>
    request<ApiEndpoint>('PUT', '/api/admin/endpoints/doc', body, token),
  outputDetail: (token: string, key: string) =>
    request<OutputDetail>('GET', `/api/admin/endpoints/output?key=${encodeURIComponent(key)}`, undefined, token),
  outputProfiles: (token: string) => request<OutputProfile[]>('GET', '/api/admin/endpoints/output/profiles', undefined, token),
  saveOutput: (token: string, body: OutputSaveRequest) => request<OutputProfile>('PUT', '/api/admin/endpoints/output/profiles', body, token),
  deleteOutput: (token: string, profileId: number) =>
    request<null>('DELETE', `/api/admin/endpoints/output/profiles/${profileId}`, undefined, token),
  system: (token: string) => request<SystemInfo>('GET', '/api/admin/system', undefined, token),
  sapCalls: (token: string, f: SapCallFilter) => request<SapCallPage>('GET', `/api/admin/sap-calls${sapCallQuery(f)}`, undefined, token),
  sapCall: (token: string, id: number) => request<SapCallDetail>('GET', `/api/admin/sap-calls/${id}`, undefined, token),
  sapCallWriter: (token: string) => request<SapCallWriterStats>('GET', '/api/admin/sap-calls/writer', undefined, token),
  dbSyncStatus: (token: string) => request<DbSyncStatus>('GET', '/api/admin/db-sync', undefined, token),
  dbSyncCompare: (token: string) => request<DbCompareRow[]>('GET', '/api/admin/db-sync/compare', undefined, token),
  dbSyncRun: (token: string, body: { mode: DbImportMode; dryRun: boolean; migrate: boolean; confirmTarget?: string | null }) =>
    request<DbSyncRunResult>('POST', '/api/admin/db-sync/run', body, token),
  /** Tải file (tài liệu TS / OpenAPI nội bộ) — cần token nên không dùng thẻ <a href> trực tiếp. */
  download: (token: string, path: string, fallbackName: string) => download(path, token, fallbackName)
};

async function download(path: string, token: string, fallbackName: string) {
  let res: Response;
  try {
    res = await fetch(`${BASE}${path}`, { headers: { Authorization: `Bearer ${token}` } });
  } catch {
    throw new ApiError(0, 'Không kết nối được RDM API.');
  }
  if (res.status === 401) {
    onUnauthorized?.();
    throw new ApiError(401, 'Phiên đăng nhập hết hạn — đăng nhập lại.');
  }
  if (!res.ok) throw new ApiError(res.status, `Tải file lỗi HTTP ${res.status}.`);
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(res.headers.get('Content-Disposition') ?? '')?.[1] ?? fallbackName;
  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement('a');
  a.href = url;
  a.download = decodeURIComponent(name);
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

/** Từ khóa kỹ thuật SAP thường gặp — cảnh báo (không chặn) khi soạn mô tả cho đối tác. */
const SAP_TERMS = /\b(SAP|ECC|BAPI\w*|RFC\w*|NCo|ABAP|BDC|SE37|VK1[1-3]|VA0[1-3]|VA4[1-3]|VBN1|FB0[1-3]|FB75|MM17|KNUMH|KONP|KONH|KFRST|KBSTAT|VBAK|VBAP|BKPF|BSEG|MARA|MLAN|KNA1|KNKK|TVKO|T001W|A9\d\d|Z_E\w+|Z\w*_RDM\w*)\b/gi;
export function findSapTerms(...texts: (string | null | undefined)[]): string[] {
  const found = new Set<string>();
  // Bỏ qua đường dẫn API (VD /api/sd/prices-bapi/...) — đối tác bắt buộc dùng đúng URL đó.
  for (const t of texts) for (const m of (t ?? '').replace(/\/api\/\S*/gi, ' ').matchAll(SAP_TERMS)) found.add(m[0]);
  return [...found];
}

export function formatTime(utc: string | null): string {
  if (!utc) return '';
  const d = new Date(utc.endsWith('Z') ? utc : `${utc}Z`);
  return Number.isNaN(d.getTime()) ? utc : d.toLocaleString('vi-VN');
}
