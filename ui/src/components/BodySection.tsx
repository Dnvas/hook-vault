import type { ReactNode } from 'react'

interface BodySectionProps {
  body: string
}

export function BodySection({ body }: BodySectionProps) {
  let display = body
  try {
    display = JSON.stringify(JSON.parse(body), null, 2)
  } catch {
    /* keep raw */
  }

  return (
    <section>
      <SectionHeader>Body</SectionHeader>
      <pre
        className="text-xs font-mono text-slate-300 bg-slate-900/70 rounded-lg p-3
                   overflow-x-auto whitespace-pre-wrap break-words border border-slate-700/40
                   leading-relaxed"
      >
        {display}
      </pre>
    </section>
  )
}

export function SectionHeader({ children }: { children: ReactNode }) {
  return (
    <h3 className="text-xs font-semibold text-indigo-400 uppercase tracking-widest mb-2">
      {children}
    </h3>
  )
}
