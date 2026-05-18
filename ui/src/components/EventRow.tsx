import type { EventSummary, EventStatus } from '../types'

interface EventRowProps {
  event: EventSummary
  selected: boolean
  onClick: () => void
}

function statusBorderColor(status: EventStatus): string {
  switch (status) {
    case 'Forwarded':
      return 'border-green-500'
    case 'ForwardFailed':
    case 'ReplayFailed':
      return 'border-red-500'
    case 'Replaying':
      return 'border-amber-500'
    case 'Captured':
      return 'border-sky-500'
    default:
      return 'border-slate-600'
  }
}

function statusDotColor(status: EventStatus): string {
  switch (status) {
    case 'Forwarded':
      return 'bg-green-400'
    case 'ForwardFailed':
    case 'ReplayFailed':
      return 'bg-red-400'
    case 'Replaying':
      return 'bg-amber-400 animate-pulse'
    case 'Captured':
      return 'bg-sky-400'
    default:
      return 'bg-slate-500'
  }
}

function sigBadge(valid: boolean | null) {
  if (valid === null) return null
  return valid ? (
    <span className="text-green-400 text-xs leading-none">✓</span>
  ) : (
    <span className="text-red-400 text-xs leading-none">✗</span>
  )
}

export function EventRow({ event, selected, onClick }: EventRowProps) {
  const time = new Date(event.receivedAt).toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })

  return (
    <button
      data-testid="event-row"
      onClick={onClick}
      className={`w-full text-left px-3 py-2.5 border-l-2 transition-all duration-150 group
                  ${statusBorderColor(event.status)}
                  ${
                    selected
                      ? 'bg-indigo-950/50 shadow-[inset_1px_0_0_0_rgba(99,102,241,0.3)]'
                      : 'hover:bg-slate-800/50'
                  }`}
    >
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <span className={`w-1.5 h-1.5 rounded-full shrink-0 ${statusDotColor(event.status)}`} />
          <span className="text-sm font-medium text-white truncate">{event.provider}</span>
        </div>
        <div className="flex items-center gap-1.5 shrink-0">
          {sigBadge(event.signatureValid)}
          {event.forwardStatusCode != null && (
            <span
              className={`text-xs font-mono ${event.forwardStatusCode < 300 ? 'text-green-400' : 'text-red-400'}`}
            >
              {event.forwardStatusCode}
            </span>
          )}
        </div>
      </div>
      <div className="flex items-center justify-between mt-0.5">
        <span className="text-xs text-slate-500 font-mono truncate">{event.path}</span>
        <span className="text-xs text-slate-600 font-mono shrink-0 ml-2">{time}</span>
      </div>
    </button>
  )
}
