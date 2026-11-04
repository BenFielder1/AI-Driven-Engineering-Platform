import { Clock, Eye, FlaskConical, Globe, KeyRound, ListChecks, Lock, ShieldCheck, UserRound, type LucideIcon } from 'lucide-react'

import { panelClass } from '../../../components/shared/panel-class'
import { cn } from '../../../lib/utils'
import { PageIntro } from '../components/page-intro'
import { SiteLink } from '../components/site-link'
import { pageTitles } from '../content/headings'
import { marketingPages } from '../content/pages'
import { securityFacts, type SecurityFact } from '../content/security'

const page = marketingPages['/security']

const icons: Record<SecurityFact['icon'], LucideIcon> = {
  key: KeyRound,
  user: UserRound,
  clock: Clock,
  eye: Eye,
  lock: Lock,
  list: ListChecks,
  shield: ShieldCheck,
  globe: Globe,
  flask: FlaskConical,
}

/** /security (FR-217): factual statements, each with its docs link. */
export function SecurityPage() {
  return (
    <>
      <PageIntro eyebrow={page.name} title={pageTitles.security}>
        {page.description}
      </PageIntro>
      <ul className="mx-auto grid max-w-6xl gap-4 px-4 pb-20 sm:grid-cols-2 lg:grid-cols-3">
        {securityFacts.map((fact) => {
          const Icon = icons[fact.icon]
          return (
            <li key={fact.id} id={fact.id} className={cn(panelClass, 'flex flex-col p-6')} data-testid="security-fact">
              <Icon aria-hidden="true" className="size-5 text-primary" />
              <h2 className="mt-3 text-lg font-semibold tracking-tight">{fact.title}</h2>
              <p className="mt-2 flex-1 text-sm text-muted-foreground">{fact.body}</p>
              <p className="mt-4 text-sm">
                <SiteLink link={fact.link} className="inline-flex min-h-6 items-center font-medium text-primary underline-offset-4 hover:underline" />
              </p>
            </li>
          )
        })}
      </ul>
    </>
  )
}
