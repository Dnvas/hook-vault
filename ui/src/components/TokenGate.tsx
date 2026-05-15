import { useState } from 'react'
import { setToken } from '../api/client'

interface TokenGateProps {
  onToken: () => void
}

export function TokenGate({ onToken }: TokenGateProps) {
  const [value, setValue] = useState('')
  const [error, setError] = useState('')

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const trimmed = value.trim()
    if (!trimmed) {
      setError('Token is required')
      return
    }
    setToken(trimmed)
    onToken()
  }

  return (
    <div className="min-h-screen bg-slate-900 flex items-center justify-center">
      {/* Subtle grid background for depth */}
      <div
        className="absolute inset-0 pointer-events-none"
        style={{
          backgroundImage:
            'linear-gradient(rgba(99,102,241,0.04) 1px, transparent 1px), linear-gradient(90deg, rgba(99,102,241,0.04) 1px, transparent 1px)',
          backgroundSize: '32px 32px',
        }}
      />

      <div className="relative w-full max-w-md mx-4">
        {/* Logo area */}
        <div className="mb-8 text-center">
          <div className="inline-flex items-center gap-2.5 mb-3">
            <div className="w-7 h-7 rounded-lg bg-indigo-600 flex items-center justify-center">
              <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
                <path d="M2 4h10M2 7h6M2 10h8" stroke="white" strokeWidth="1.5" strokeLinecap="round" />
              </svg>
            </div>
            <span className="text-white font-semibold text-lg tracking-tight">HookVault</span>
          </div>
          <p className="text-slate-500 text-sm font-mono">webhook capture + replay</p>
        </div>

        {/* Card */}
        <div className="bg-slate-800 border border-slate-700/60 rounded-xl p-6 shadow-2xl shadow-black/40">
          <h2 className="text-sm font-semibold text-slate-300 mb-1">Access token required</h2>
          <p className="text-slate-500 text-xs mb-4">
            Generate one with:
          </p>
          <pre className="mb-5 text-xs bg-slate-900 text-indigo-300 rounded-lg px-3 py-2.5 font-mono border border-slate-700/50">
            docker compose run --rm hookvault generate-token
          </pre>

          <form onSubmit={handleSubmit} className="space-y-3">
            <input
              type="password"
              value={value}
              onChange={(e) => setValue(e.target.value)}
              placeholder="eyJhbGci…"
              autoFocus
              className="w-full bg-slate-900 border border-slate-600 rounded-lg px-3.5 py-2.5
                         text-white placeholder-slate-600 text-sm font-mono
                         focus:outline-none focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500/50
                         transition-colors"
            />
            {error && <p className="text-xs text-red-400">{error}</p>}
            <button
              type="submit"
              className="w-full bg-indigo-600 hover:bg-indigo-500 active:bg-indigo-700
                         text-white font-medium rounded-lg py-2.5 text-sm
                         transition-colors duration-150"
            >
              Continue
            </button>
          </form>
        </div>

        <p className="mt-4 text-center text-xs text-slate-600">
          Token stored in <code className="font-mono text-slate-500">sessionStorage</code>
        </p>
      </div>
    </div>
  )
}
