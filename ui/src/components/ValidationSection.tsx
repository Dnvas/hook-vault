import type { ValidationDetails } from '../types'
import { SectionHeader } from './BodySection'

interface ValidationSectionProps {
  valid: boolean | null
  details: ValidationDetails | null
}

export function ValidationSection({ valid, details }: ValidationSectionProps) {
  return (
    <section>
      <SectionHeader>Validation</SectionHeader>
      {valid === null ? (
        <p className="text-xs text-slate-600 font-mono">No validation configured for this provider.</p>
      ) : (
        <div className="space-y-3">
          <div
            className={`inline-flex items-center gap-2 px-3 py-1.5 rounded-full text-xs font-medium
                         ${valid
                           ? 'bg-green-900/30 text-green-300 border border-green-800/40'
                           : 'bg-red-900/30 text-red-300 border border-red-800/40'
                         }`}
          >
            {valid ? '✓ Signature valid' : '✗ Signature invalid'}
          </div>
          {details && (
            <dl className="text-xs space-y-2 mt-1">
              <Row label="Algorithm" value={details.algorithmUsed} />
              <Row label="Payload used" value={details.payloadUsed} mono />
              <Row label="Computed" value={details.computedSignature} mono />
              <Row label="Received" value={details.receivedSignature} mono />
              {details.extractedTimestamp && (
                <Row label="Timestamp" value={details.extractedTimestamp} />
              )}
            </dl>
          )}
        </div>
      )}
    </section>
  )
}

function Row({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex gap-3 min-w-0">
      <dt className="text-slate-500 w-24 shrink-0 pt-px">{label}</dt>
      <dd className={`text-slate-300 break-all min-w-0 ${mono ? 'font-mono' : ''}`}>{value}</dd>
    </div>
  )
}
