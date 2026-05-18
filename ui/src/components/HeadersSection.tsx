import { SectionHeader } from './BodySection'

interface HeadersSectionProps {
  headers: Record<string, string>
}

export function HeadersSection({ headers }: HeadersSectionProps) {
  const entries = Object.entries(headers)

  return (
    <section>
      <SectionHeader>Headers</SectionHeader>
      {entries.length === 0 ? (
        <p className="text-xs text-slate-600 font-mono">No headers captured.</p>
      ) : (
        <table className="w-full text-xs">
          <tbody>
            {entries.map(([k, v]) => (
              <tr
                key={k}
                className="border-b border-slate-700/30 last:border-0"
              >
                <td className="py-1.5 pr-4 text-slate-500 font-mono align-top w-2/5 break-all">
                  {k}
                </td>
                <td className="py-1.5 text-slate-300 font-mono break-all">
                  {v}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
