import { Bell, Check, GitPullRequest, Layers, ListTodo, ShieldCheck, SquareTerminal, type LucideIcon } from 'lucide-react'
import type { ReactNode } from 'react'

import { cn } from '../../../lib/utils'
import { PageIntro } from '../components/page-intro'
import { ArtifactsPreview, DeploymentsPreview, GatesPreview, RunsPreview } from '../components/previews/list-previews'
import { pageTitles } from '../content/headings'
import { marketingPages } from '../content/pages'
import { productSections, type ProductIcon, type ProductPreview, type ProductSection } from '../content/product'

const page = marketingPages['/product']

const previews: Record<ProductPreview, () => ReactNode> = {
  gates: () => <GatesPreview />,
  artifacts: () => <ArtifactsPreview />,
  runs: () => <RunsPreview />,
  deployments: () => <DeploymentsPreview />,
}

const icons: Record<ProductIcon, LucideIcon> = {
  git: GitPullRequest,
  tracker: ListTodo,
  layers: Layers,
  bell: Bell,
  shield: ShieldCheck,
  terminal: SquareTerminal,
}

function Visual({ visual }: { visual: ProductSection['visual'] }) {
  if ('preview' in visual) {
    return previews[visual.preview]()
  }
  const Icon = icons[visual.icon]
  return (
    <div className="hidden items-center justify-center lg:flex" aria-hidden="true">
      <span className="flex size-28 items-center justify-center rounded-3xl border border-border bg-muted">
        <Icon className="size-12 text-primary" />
      </span>
    </div>
  )
}

function ProductArea({ section, flip }: { section: ProductSection; flip: boolean }) {
  const headingId = `${section.id}-heading`
  return (
    <section id={section.id} aria-labelledby={headingId} className="scroll-mt-20 border-t border-border" data-testid="product-section">
      <div className="mx-auto grid max-w-6xl items-center gap-10 px-4 py-14 sm:py-20 lg:grid-cols-2">
        <div className={cn(flip && 'lg:order-2')}>
          <h2 id={headingId} className="text-2xl font-semibold tracking-tight sm:text-3xl">
            {section.title}
          </h2>
          {section.body.map((p) => (
            <p key={p} className="mt-3 text-muted-foreground">
              {p}
            </p>
          ))}
          <ul className="mt-5 flex flex-col gap-2 text-sm">
            {section.bullets.map((b) => (
              <li key={b} className="flex gap-2">
                <Check aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-success" />
                {b}
              </li>
            ))}
          </ul>
        </div>
        <Visual visual={section.visual} />
      </div>
    </section>
  )
}

/** /product (FR-213): one anchored section per feature area, with a preview (FR-220) or an icon. */
export function ProductPage() {
  return (
    <>
      <PageIntro eyebrow={page.name} title={pageTitles.product}>
        {page.description}
      </PageIntro>
      <nav aria-label="On this page" className="mx-auto max-w-6xl px-4 pb-10">
        <ul className="flex flex-wrap gap-2">
          {productSections.map((s) => (
            <li key={s.id}>
              <a
                href={`#${s.id}`}
                className="inline-flex min-h-8 items-center rounded-full border border-border px-3 text-sm text-muted-foreground transition-colors hover:text-foreground"
              >
                {s.title}
              </a>
            </li>
          ))}
        </ul>
      </nav>
      {productSections.map((section, i) => (
        <ProductArea key={section.id} section={section} flip={i % 2 === 1} />
      ))}
    </>
  )
}
