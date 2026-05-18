import type { EventDetail } from '../types'
import { SectionHeader } from './BodySection'

type ForwardProps = Pick<
  EventDetail,
  | 'forwardUrl'
  | 'forwardStatusCode'
  | 'forwardError'
  | 'forwardedAt'
  | 'replayCount'
  | 'lastReplayAt'
  | 'lastError'
>

export function ForwardSection(props: ForwardProps) {
  const {
    forwardUrl,
    forwardStatusCode,
    forwardError,
    forwardedAt,
    replayCount,
    lastReplayAt,
    lastError,
  } = props

  return (
    <section>
      <SectionHeader>Forward</SectionHeader>
      <dl className="text-xs space-y-2">
        <Row label="Destination" value={forwardUrl} />
        {forwardStatusCode != null && (
          <Row
            label="Status"
            value={forwardStatusCode.toString()}
            valueClass={
              forwardStatusCode < 300 ? 'text-green-400' : 'text-red-400'
            }
          />
        )}
        {forwardError && (
          <Row label="Error" value={forwardError} valueClass="text-red-400" />
        )}
        {forwardedAt && (
          <Row
            label="Forwarded"
            value={new Date(forwardedAt).toLocaleString()}
          />
        )}
        {replayCount > 0 && <Row label="Replays" value={String(replayCount)} />}
        {lastReplayAt && (
          <Row
            label="Last replay"
            value={new Date(lastReplayAt).toLocaleString()}
          />
        )}
        {lastError && (
          <Row label="Last error" value={lastError} valueClass="text-red-400" />
        )}
      </dl>
    </section>
  )
}

function Row({
  label,
  value,
  valueClass = 'text-slate-300',
}: {
  label: string
  value: string
  valueClass?: string
}) {
  return (
    <div className="flex gap-3 min-w-0">
      <dt className="text-slate-500 w-24 shrink-0 pt-px">{label}</dt>
      <dd className={`break-all min-w-0 font-mono ${valueClass}`}>{value}</dd>
    </div>
  )
}
