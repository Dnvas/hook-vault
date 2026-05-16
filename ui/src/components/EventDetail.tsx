import { useState } from 'react'
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
  const [editing, setEditing] = useState(false)
  const [editedBody, setEditedBody] = useState('')
  const [submitting, setSubmitting] = useState(false)

  async function handleReplay() {
    await api.replayEvent(id)
    invalidateEvent(id)
    invalidateEvents()
  }

  const handleEditReplay = async () => {
    setSubmitting(true)
    try {
      await api.replayEvent(id, editedBody)
      setEditing(false)
      invalidateEvent(id)
      invalidateEvents()
    } finally {
      setSubmitting(false)
    }
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
        <div className="flex gap-2 shrink-0">
          <button
            onClick={() => {
              setEditedBody(data.body)
              setEditing(true)
            }}
            disabled={editing || !data.body}
            className="px-3 py-1.5 bg-slate-700 hover:bg-slate-600 active:bg-slate-800
                       disabled:opacity-50 disabled:cursor-not-allowed
                       text-white text-xs font-medium rounded-lg transition-colors duration-150"
          >
            ✎ Edit &amp; Replay
          </button>
          <button
            onClick={handleReplay}
            className="px-3 py-1.5 bg-indigo-600 hover:bg-indigo-500 active:bg-indigo-700
                       text-white text-xs font-medium rounded-lg transition-colors duration-150"
          >
            ↺ Replay
          </button>
        </div>
      </div>

      {/* Inline body editor — visible when editing is true */}
      {editing && (
        <div className="px-4 py-4 border-b border-slate-700/60 bg-slate-900/40 shrink-0">
          <label className="block text-xs font-semibold text-indigo-400 uppercase tracking-widest mb-2">
            Edit body
          </label>
          <textarea
            value={editedBody}
            onChange={e => setEditedBody(e.target.value)}
            rows={10}
            className="w-full px-3 py-2 text-sm font-mono
                       bg-slate-950 text-slate-100 border border-slate-700
                       rounded focus:outline-none focus:border-indigo-500"
          />
          <div className="mt-2 flex gap-2 justify-end">
            <button
              onClick={() => setEditing(false)}
              disabled={submitting}
              className="px-3 py-1.5 text-xs font-medium rounded-lg
                         bg-slate-700 hover:bg-slate-600 disabled:opacity-50
                         text-slate-300 transition-colors duration-150"
            >
              Cancel
            </button>
            <button
              onClick={handleEditReplay}
              disabled={submitting}
              className="px-3 py-1.5 text-xs font-medium rounded-lg
                         bg-indigo-600 hover:bg-indigo-500 disabled:opacity-60
                         text-white transition-colors duration-150"
            >
              {submitting ? 'Replaying…' : 'Replay with edits'}
            </button>
          </div>
        </div>
      )}

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
