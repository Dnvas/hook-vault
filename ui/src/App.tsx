import { useState } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { getToken, setToken } from './api/client'
import { TokenGate } from './components/TokenGate'

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

  if (!hasToken) {
    return <TokenGate onToken={() => setHasToken(true)} />
  }

  // Split pane placeholder — replaced in Task 10
  return (
    <div className="h-screen bg-slate-900 flex overflow-hidden">
      <div className="w-72 shrink-0 border-r border-slate-700/60 flex items-center justify-center">
        <span className="text-slate-600 text-xs font-mono">event list — coming soon</span>
      </div>
      <div className="flex-1 flex items-center justify-center">
        <span className="text-slate-600 text-xs font-mono">event detail — coming soon</span>
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
