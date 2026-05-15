import type { EventDetail, HealthResponse, ListEventsResponse } from '../types'

const TOKEN_KEY = 'hv_token'

export function getToken(): string | null {
  return sessionStorage.getItem(TOKEN_KEY)
}

export function setToken(token: string): void {
  sessionStorage.setItem(TOKEN_KEY, token)
}

export function clearToken(): void {
  sessionStorage.removeItem(TOKEN_KEY)
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getToken()
  const res = await fetch(path, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init?.headers,
    },
  })

  if (res.status === 401) {
    clearToken()
    window.location.reload()
    throw new Error('Unauthorized')
  }

  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || `HTTP ${res.status}`)
  }

  return res.json() as Promise<T>
}

export const api = {
  getHealth: () => request<HealthResponse>('/api/health'),

  listEvents: (params: { provider?: string; status?: string; limit?: number; offset?: number }) => {
    const q = new URLSearchParams()
    if (params.provider) q.set('provider', params.provider)
    if (params.status) q.set('status', params.status)
    if (params.limit != null) q.set('limit', String(params.limit))
    if (params.offset != null) q.set('offset', String(params.offset))
    return request<ListEventsResponse>(`/api/events?${q}`)
  },

  getEvent: (id: string) => request<EventDetail>(`/api/events/${id}`),

  replayEvent: (id: string) => request<unknown>(`/api/events/${id}/replay`, { method: 'POST' }),

  replayFailed: () => request<unknown>('/api/events/replay-failed', { method: 'POST' }),

  deleteEvents: (provider?: string) => {
    const q = provider ? `?provider=${encodeURIComponent(provider)}` : ''
    return request<unknown>(`/api/events${q}`, { method: 'DELETE' })
  },
}
