import type {
  Alarm,
  Dashboard,
  DocumentDetail,
  DocumentSummary,
  EpcImportOutcome,
  GateCycle,
  GateSnapshot,
  Paged,
  Reader,
  Session,
  WarehouseUser,
} from './types'

const TOKEN_KEY = 'warehouse.session'

/**
 * Session storage.
 *
 * Kept in sessionStorage rather than localStorage: a shared warehouse terminal
 * should not stay signed in after the browser closes.
 */
export const session = {
  get(): Session | null {
    try {
      const raw = sessionStorage.getItem(TOKEN_KEY)
      if (!raw) return null

      const parsed = JSON.parse(raw) as Session
      return new Date(parsed.expiresAt) > new Date() ? parsed : null
    } catch {
      return null
    }
  },
  set(value: Session) {
    sessionStorage.setItem(TOKEN_KEY, JSON.stringify(value))
  },
  clear() {
    sessionStorage.removeItem(TOKEN_KEY)
  },
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly offending: string[] = [],
  ) {
    super(message)
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = session.get()?.token
  const headers = new Headers(init.headers)

  if (!(init.body instanceof FormData)) {
    headers.set('Content-Type', 'application/json')
  }

  if (token) headers.set('Authorization', `Bearer ${token}`)

  const response = await fetch(path, { ...init, headers })

  if (response.status === 401) {
    session.clear()
    window.location.hash = '#/login'
    throw new ApiError('Your session has expired. Sign in again.', 401)
  }

  if (!response.ok) {
    // The API returns ProblemDetails, with the offending values attached for
    // validation failures so the UI can point at the exact EPCs.
    let message = `${response.status} ${response.statusText}`
    let offending: string[] = []

    try {
      const problem = await response.json()
      message = problem.detail || problem.title || message
      offending = problem.offending ?? []
    } catch {
      /* response had no JSON body */
    }

    throw new ApiError(message, response.status, offending)
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

const post = <T>(path: string, body?: unknown) =>
  request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })

export const api = {
  login: (userName: string, password: string) =>
    request<Session>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ userName, password }),
    }),

  changePassword: (currentPassword: string, newPassword: string) =>
    post<void>('/api/auth/change-password', { currentPassword, newPassword }),

  dashboard: () => request<Dashboard>('/api/dashboard'),

  gates: () => request<GateSnapshot[]>('/api/gates'),
  gate: (code: string) => request<GateSnapshot>(`/api/gates/${encodeURIComponent(code)}/status`),
  armGate: (code: string) => post<GateSnapshot>(`/api/gates/${encodeURIComponent(code)}/start`),
  disarmGate: (code: string) => post<GateSnapshot>(`/api/gates/${encodeURIComponent(code)}/stop`),
  cycles: (code: string, take = 50) =>
    request<GateCycle[]>(`/api/gates/${encodeURIComponent(code)}/cycles?take=${take}`),

  documents: (params: Record<string, string | number | undefined> = {}) => {
    const query = new URLSearchParams()

    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined && value !== '') query.set(key, String(value))
    }

    return request<Paged<DocumentSummary>>(`/api/documents?${query}`)
  },

  document: (id: number) => request<DocumentDetail>(`/api/documents/${id}`),

  createDocument: (type: 'inward' | 'outward', body: unknown) =>
    post<DocumentDetail>(`/api/documents/${type}`, body),

  releaseDocument: (id: number, gateCode: string) =>
    post<DocumentDetail>(`/api/documents/${id}/release`, { gateCode }),

  cancelDocument: (id: number, reason: string) =>
    post<DocumentDetail>(`/api/documents/${id}/cancel`, { reason }),

  retryDocument: (id: number) => post<DocumentDetail>(`/api/documents/${id}/retry`),

  updateDocument: (
    id: number,
    body: { reference?: string; notes?: string; epcs?: string[] },
  ) => request<DocumentDetail>(`/api/documents/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }),

  deleteDocument: (id: number) =>
    request<void>(`/api/documents/${id}`, { method: 'DELETE' }),

  users: () => request<WarehouseUser[]>('/api/users'),

  roles: () => request<{ name: string; description?: string }[]>('/api/users/roles'),

  createUser: (body: {
    userName: string
    displayName: string
    email?: string
    password: string
    roles: string[]
  }) => post<WarehouseUser>('/api/users', body),

  updateUser: (
    id: number,
    body: { displayName?: string; email?: string; roles?: string[]; isActive?: boolean },
  ) => request<WarehouseUser>(`/api/users/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }),

  resetUserPassword: (id: number, newPassword: string) =>
    post<void>(`/api/users/${id}/reset-password`, { newPassword, mustChangePassword: true }),

  /** Removes the account, or deactivates it when it has history. Says which. */
  deleteUser: (id: number) =>
    request<{ deactivated: boolean; message: string }>(`/api/users/${id}`, { method: 'DELETE' }),

  readers: () => request<Reader[]>('/api/rfid/readers'),
  connectReader: (id: string) => post<unknown>(`/api/rfid/readers/${encodeURIComponent(id)}/connect`),
  disconnectReader: (id: string) =>
    post<unknown>(`/api/rfid/readers/${encodeURIComponent(id)}/disconnect`),

  alarms: (status?: string) =>
    request<Alarm[]>(`/api/alarms${status ? `?status=${status}` : ''}`),

  acknowledgeAlarm: (id: number) => post<void>(`/api/alarms/${id}/acknowledge`),
  resolveAlarm: (id: number, notes: string) => post<void>(`/api/alarms/${id}/resolve`, { notes }),

  /**
   * Uploads a catalogue, optionally planning documents from what it brought in.
   * Planning uses the file's own rows and order, not the whole catalogue, so an
   * import never sweeps up stock the operator did not mention.
   */
  importEpcs: (
    file: File,
    options: {
      updateExisting: boolean
      generateDocuments?: boolean
      documentType?: 'Inward' | 'Outward'
      epcsPerDocument?: number
      gateCode?: string
    },
  ) => {
    const form = new FormData()
    form.append('file', file)

    const query = new URLSearchParams({ updateExisting: String(options.updateExisting) })

    if (options.generateDocuments) {
      query.set('generateDocuments', 'true')
      query.set('documentType', options.documentType ?? 'Inward')

      if (options.epcsPerDocument) query.set('epcsPerDocument', String(options.epcsPerDocument))
      if (options.gateCode) query.set('gateCode', options.gateCode)
    }

    return request<EpcImportOutcome>(`/api/epcs/import?${query}`, { method: 'POST', body: form })
  },
}
