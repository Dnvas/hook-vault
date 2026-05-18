import { useState } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { getToken, setToken } from './api/client'
import { TokenGate } from './components/TokenGate'
import { EventList } from './components/EventList'
import { EventDetail } from './components/EventDetail'

const queryClient = new QueryClient()

function Inner() {
  const [hasToken, setHasToken] = useState(() => {
    // Read ?token= from URL on initial mount, store it, then strip from address bar.
    const params = new URLSearchParams(window.location.search)
    const urlToken = params.get('token')
    if (urlToken) {
      setToken(urlToken)
      params.delete('token')
      const newUrl = params.toString()
        ? `${window.location.pathname}?${params}`
        : window.location.pathname
      window.history.replaceState({}, '', newUrl)
    }
    return !!getToken()
  })

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [providerFilter, setProviderFilter] = useState('')
  const [statusFilter, setStatusFilter] = useState('')

  if (!hasToken) {
    return <TokenGate onToken={() => setHasToken(true)} />
  }

  return (
    <div className="h-screen bg-slate-900 flex overflow-hidden">
      <div className="w-72 shrink-0 border-r border-slate-700/60 flex flex-col">
        {/* Brand header */}
        <div className="flex items-center gap-2.5 px-3 py-2.5 border-b border-slate-700/60 shrink-0">
          <div className="w-5 h-5 rounded-md bg-indigo-600 flex items-center justify-center shrink-0">
            <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
              <path
                d="M1 3h8M1 5h5M1 7h6"
                stroke="white"
                strokeWidth="1.2"
                strokeLinecap="round"
              />
            </svg>
          </div>
          <span className="text-sm font-semibold text-white tracking-tight">
            HookVault
          </span>
        </div>
        <EventList
          selectedId={selectedId}
          onSelect={setSelectedId}
          providerFilter={providerFilter}
          statusFilter={statusFilter}
          onProviderFilter={setProviderFilter}
          onStatusFilter={setStatusFilter}
        />
      </div>
      <div className="flex-1 overflow-hidden">
        {selectedId ? (
          <EventDetail id={selectedId} />
        ) : (
          <div className="h-full flex flex-col items-center justify-center gap-2">
            <div className="w-10 h-10 rounded-xl bg-slate-800 border border-slate-700/60 flex items-center justify-center mb-1">
              <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
                <path
                  d="M2 5h14M2 9h9M2 13h11"
                  stroke="rgba(99,102,241,0.5)"
                  strokeWidth="1.5"
                  strokeLinecap="round"
                />
              </svg>
            </div>
            <p className="text-slate-600 text-xs font-mono">
              Select an event to inspect
            </p>
          </div>
        )}
      </div>
    </div>
  )
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <Inner />
    </QueryClientProvider>
  )
}
