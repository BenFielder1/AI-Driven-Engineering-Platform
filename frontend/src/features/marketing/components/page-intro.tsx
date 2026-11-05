import type { ReactNode } from 'react'

/** The top of a marketing page: an eyebrow, the page's only `h1` (FR-210) and a lead paragraph. */
export function PageIntro({ eyebrow, title, children }: { eyebrow?: string; title: string; children?: ReactNode }) {
  return (
    <section className="mx-auto max-w-6xl px-4 pt-16 pb-12 sm:pt-24">
      {eyebrow && <p className="eyebrow text-primary">{eyebrow}</p>}
      <h1 className="mt-3 max-w-3xl text-3xl font-semibold tracking-tight text-balance sm:text-5xl">{title}</h1>
      {children && <p className="mt-4 max-w-2xl text-lg text-muted-foreground">{children}</p>}
    </section>
  )
}
