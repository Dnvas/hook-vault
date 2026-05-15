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
          <div className="h-full flex items-center justify-center">
            <p className="text-slate-700 text-sm font-mono">Select an event to inspect</p>
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
