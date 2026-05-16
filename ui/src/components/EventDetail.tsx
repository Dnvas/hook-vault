import { useEvent, useInvalidateEvent } from '../hooks/useEvent'
import { useInvalidateEvents } from '../hooks/useEvents'
import { api } from '../api/client'
import { BodySection } from './BodySection'
import { HeadersSection } from './HeadersSection'
import { ValidationSection } from './ValidationSection'
import { ForwardSection } from './ForwardSection'
import type { EventStatus } from '../types'

interface EventDetailProps {
  id: string
}

function StatusBadge({ status }: { status: EventStatus }) {
  const colorMap: Record<EventStatus, string> = {
    Received: 'bg-slate-700 text-slate-300',
    Forwarding: 'bg-amber-900/40 text-amber-300',
    Forwarded: 'bg-green-900/40 text-green-300',
    ForwardFailed: 'bg-red-900/40 text-red-300',
    Replaying: 'bg-amber-900/40 text-amber-300',
    ReplayFailed: 'bg-red-900/40 text-red-300',
    Captured: 'bg-sky-900/40 text-sky-300',
  }

  return (
    <span
      className={`px-2 py-0.5 rounded text-xs font-medium font-mono ${colorMap[status] ?? 'bg-slate-700 text-slate-300'}`}
    >
      {status}
    </span>
  )
}

export function EventDetail({ id }: EventDetailProps) {
  const { data, isLoading, error } = useEvent(id)
  const invalidateEvent = useInvalidateEvent()
  const invalidateEvents = useInvalidateEvents()

  async function handleReplay() {
    await api.replayEvent(id)
    invalidateEvent(id)
    invalidateEvents()
  }

  if (isLoading) {
    return (
      <div className="h-full flex items-center justify-center">
        <span className="text-slate-600 text-xs font-mono">Loading…</span>
      </div>
    )
  }

  if (error || !data) {
    return (
      <div className="h-full flex items-center justify-center">
        <span className="text-red-400 text-xs font-mono">Failed to load event</span>
      </div>
    )
  }

  const time = new Date(data.receivedAt).toLocaleString()

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Panel header */}
      <div className="flex items-start justify-between gap-4 px-4 py-3 border-b border-slate-700/60 shrink-0">
        <div className="min-w-0">
          <div className="flex items-center gap-2 mb-1 flex-wrap">
            <span className="text-sm font-semibold text-white">{data.provider}</span>
            <StatusBadge status={data.status} />
            {data.status === 'Captured' && (
              <span className="text-xs font-mono uppercase tracking-wider px-2 py-0.5 rounded
                               bg-sky-950/60 text-sky-300 border border-sky-700/60">
                capture only
              </span>
            )}
          </div>
          <div className="text-xs text-slate-500 font-mono truncate">
            POST {data.path} · {time}
          </div>
        </div>
        <button
          onClick={handleReplay}
          className="shrink-0 px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 active:bg-indigo-700
                     text-white text-xs font-medium rounded-lg transition-colors duration-150"
        >
          ↺ Replay
        </button>
      </div>

      {/* Scrollable content sections */}
      <div className="flex-1 overflow-y-auto min-h-0 px-4 py-5 space-y-6">
        <BodySection body={data.body} />
        <HeadersSection headers={data.headers} />
        <ValidationSection valid={data.signatureValid} details={data.validationDetails} />
        <ForwardSection
          forwardUrl={data.forwardUrl}
          forwardStatusCode={data.forwardStatusCode}
          forwardError={data.forwardError}
          forwardedAt={data.forwardedAt}
          replayCount={data.replayCount}
          lastReplayAt={data.lastReplayAt}
          lastError={data.lastError}
        />
      </div>
    </div>
  )
}
