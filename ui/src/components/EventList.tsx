import { useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api/client'
import { useEvents, useInvalidateEvents } from '../hooks/useEvents'
import { useEventStream } from '../hooks/useEventStream'
import { EventRow } from './EventRow'
import type { EventSummary } from '../types'

interface EventListProps {
  selectedId: string | null
  onSelect: (id: string) => void
  providerFilter: string
  statusFilter: string
  onProviderFilter: (p: string) => void
  onStatusFilter: (s: string) => void
}

const STATUS_OPTIONS = ['', 'Captured', 'Forwarded', 'ForwardFailed', 'Replaying', 'ReplayFailed']

export function EventList({
  selectedId,
  onSelect,
  providerFilter,
  statusFilter,
  onProviderFilter,
  onStatusFilter,
}: EventListProps) {
  const { data: health } = useQuery({
    queryKey: ['health'],
    queryFn: api.getHealth,
    staleTime: 60_000,
  })

  const { data, isLoading } = useEvents({
    provider: providerFilter || undefined,
    status: statusFilter || undefined,
  })

  const invalidate = useInvalidateEvents()
  const onNotification = useCallback(() => {
    invalidate()
  }, [invalidate])
  useEventStream(onNotification)

  const events: EventSummary[] = data?.items ?? []
  const providers = health?.providers ?? []

  return (
    <div className="flex flex-col h-full min-h-0">
      {/* Header */}
      <div className="flex items-center justify-between px-3 py-2 border-b border-slate-700/60 shrink-0">
        <span className="text-xs font-semibold text-indigo-400 uppercase tracking-widest">
          Events
        </span>
        <div className="flex items-center gap-1.5">
          <span className="w-1.5 h-1.5 rounded-full bg-green-400 animate-pulse" />
          <span className="text-xs text-green-400 font-mono">live</span>
        </div>
      </div>

      {/* Provider filter pills */}
      {providers.length > 0 && (
        <div className="flex gap-1 px-3 py-2 border-b border-slate-700/60 flex-wrap shrink-0">
          <FilterPill
            label="all"
            active={!providerFilter}
            onClick={() => onProviderFilter('')}
          />
          {providers.map((p) => (
            <FilterPill
              key={p}
              label={p}
              active={providerFilter === p}
              onClick={() => onProviderFilter(providerFilter === p ? '' : p)}
            />
          ))}
        </div>
      )}

      {/* Status filter pills */}
      <div className="flex gap-1 px-3 py-2 border-b border-slate-700/60 flex-wrap shrink-0">
        {STATUS_OPTIONS.map((s) => (
          <FilterPill
            key={s || 'all'}
            label={s || 'all'}
            active={statusFilter === s}
            onClick={() => onStatusFilter(statusFilter === s && s ? '' : s)}
          />
        ))}
      </div>

      {/* Event rows */}
      <div className="flex-1 overflow-y-auto min-h-0">
        {isLoading && (
          <div className="text-slate-600 text-xs font-mono text-center py-10">Loading…</div>
        )}
        {!isLoading && events.length === 0 && (
          <div className="px-4 py-10 text-center">
            <p className="text-slate-600 text-xs font-mono">No events yet.</p>
            <p className="text-slate-700 text-xs font-mono mt-1">
              Send a webhook to get started.
            </p>
          </div>
        )}
        {events.map((e) => (
          <EventRow
            key={e.id}
            event={e}
            selected={e.id === selectedId}
            onClick={() => onSelect(e.id)}
          />
        ))}
      </div>

      {/* Footer count */}
      {data && (
        <div className="px-3 py-1.5 border-t border-slate-700/60 shrink-0">
          <span className="text-xs text-slate-700 font-mono">
            {data.totalApproximate
              ? `${events.length} shown — refine to count exactly`
              : `${data.total ?? 0} event${(data.total ?? 0) !== 1 ? 's' : ''}`}
          </span>
        </div>
      )}
    </div>
  )
}

interface FilterPillProps {
  label: string
  active: boolean
  onClick: () => void
}

function FilterPill({ label, active, onClick }: FilterPillProps) {
  return (
    <button
      onClick={onClick}
      className={`px-2 py-0.5 rounded text-xs font-medium transition-colors duration-100 font-mono
                  ${
                    active
                      ? 'bg-indigo-600 text-white'
                      : 'bg-slate-700/60 text-slate-400 hover:text-slate-200 hover:bg-slate-700'
                  }`}
    >
      {label}
    </button>
  )
}
