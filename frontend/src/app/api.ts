export type Tokens = { accessToken: string; accessTokenExpiresAt: string; refreshToken: string }

export class ApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string, message: string) {
    super(message)
    this.status = status
    this.code = code
  }
}

const STORAGE_KEY = 'ros.tokens'
type Listener = (tokens: Tokens | null) => void
const listeners = new Set<Listener>()

function load(): Tokens | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    return raw ? (JSON.parse(raw) as Tokens) : null
  } catch {
    return null
  }
}

let tokens: Tokens | null = load()
let refreshing: Promise<boolean> | null = null

export function getTokens() {
  return tokens
}

export function setTokens(next: Tokens | null) {
  tokens = next
  if (next) localStorage.setItem(STORAGE_KEY, JSON.stringify(next))
  else localStorage.removeItem(STORAGE_KEY)
  listeners.forEach((l) => l(next))
}

export function onTokensChange(listener: Listener) {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

async function refresh(): Promise<boolean> {
  const current = tokens
  if (!current) return false
  refreshing ??= (async () => {
    try {
      const res = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: current.refreshToken }),
      })
      if (!res.ok) {
        setTokens(null)
        return false
      }
      const body = (await res.json()) as { tokens: Tokens }
      setTokens(body.tokens)
      return true
    } catch {
      return false
    } finally {
      refreshing = null
    }
  })()
  return refreshing
}

/** Returns a valid access token, refreshing it shortly before expiry. Used by SignalR too. */
export async function accessToken(): Promise<string | null> {
  if (!tokens) return null
  const expiresIn = new Date(tokens.accessTokenExpiresAt).getTime() - Date.now()
  if (expiresIn < 30_000) await refresh()
  return tokens?.accessToken ?? null
}

type RequestOptions = { method?: string; body?: unknown; signal?: AbortSignal; auth?: boolean; headers?: Record<string, string> }

export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, signal, auth = true, headers = {} } = options

  const send = async () => {
    const h: Record<string, string> = { Accept: 'application/json', ...headers }
    if (body !== undefined) h['Content-Type'] = 'application/json'
    if (auth) {
      const token = await accessToken()
      if (token) h.Authorization = `Bearer ${token}`
    }
    return fetch(path, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body), signal })
  }

  let res = await send()
  if (res.status === 401 && auth && tokens) {
    if (await refresh()) res = await send()
  }

  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? safeJson(text) : undefined
  if (!res.ok) {
    const problem = (data ?? {}) as { title?: string; code?: string; errors?: Record<string, string[]> }
    const validation = problem.errors ? Object.values(problem.errors).flat().join(' ') : ''
    throw new ApiError(res.status, problem.code ?? `http_${res.status}`, validation || problem.title || res.statusText || 'Request failed')
  }
  return data as T
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return 'Something went wrong.'
}

/**
 * Random v4 UUID. `crypto.randomUUID` exists only in secure contexts (HTTPS or localhost), so devices
 * opening the app over plain HTTP on a LAN address fall back to `getRandomValues`, which is always available.
 */
export function newId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  const b = crypto.getRandomValues(new Uint8Array(16))
  b[6] = (b[6] & 0x0f) | 0x40
  b[8] = (b[8] & 0x3f) | 0x80
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`
}
