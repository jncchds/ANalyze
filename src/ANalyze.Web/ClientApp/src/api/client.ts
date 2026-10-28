/** Thin fetch wrapper for /api/v1. Auth is an httpOnly cookie, so requests only need same-origin credentials. */

export class ApiError extends Error {
  status: number
  body: unknown
  constructor(status: number, message: string, body?: unknown) {
    super(message)
    this.status = status
    this.body = body
  }
}

const unauthorizedListeners = new Set<() => void>()

/** Called when any request returns 401 (password set and not signed in). */
export function onUnauthorized(fn: () => void) {
  unauthorizedListeners.add(fn)
  return () => {
    unauthorizedListeners.delete(fn)
  }
}

export type Query = Record<string, string | number | boolean | undefined | null>

export function qs(query?: Query) {
  if (!query) return ''
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(query)) {
    if (v !== undefined && v !== null && v !== '') p.set(k, String(v))
  }
  const s = p.toString()
  return s ? `?${s}` : ''
}

export async function api<T>(path: string, init: RequestInit & { query?: Query; json?: unknown } = {}): Promise<T> {
  const { query, json, ...rest } = init
  const headers = new Headers(rest.headers)
  if (json !== undefined) headers.set('Content-Type', 'application/json')
  const res = await fetch(`/api/v1${path}${qs(query)}`, {
    ...rest,
    headers,
    credentials: 'same-origin',
    body: json !== undefined ? JSON.stringify(json) : rest.body,
  })
  if (res.status === 401 && !path.startsWith('/auth/')) unauthorizedListeners.forEach((l) => l())
  if (res.status === 204 || res.status === 202) return undefined as T
  const text = await res.text()
  const body = text ? safeJson(text) : undefined
  if (!res.ok) {
    const b = body as Record<string, unknown> | undefined
    const message = (b && typeof b === 'object' ? String(b.detail ?? b.title ?? b.error ?? '') : '') || res.statusText || `HTTP ${res.status}`
    throw new ApiError(res.status, message, body)
  }
  return body as T
}

function safeJson(text: string) {
  try {
    return JSON.parse(text)
  } catch {
    return text
  }
}

export const fileUrl = (id: number, part: 'content' | 'thumb', query?: Query) => `/api/v1/files/${id}/${part}${qs(query)}`
