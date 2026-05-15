import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <div className="min-h-screen bg-slate-900 flex items-center justify-center">
      <span className="text-slate-400 font-mono text-sm">HookVault UI loading…</span>
    </div>
  </StrictMode>,
)
