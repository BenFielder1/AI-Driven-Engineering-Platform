import { buttonClasses } from '../../../../components/ui/button-classes'
import { Link } from '../../../../lib/navigation'
import { heroSubhead } from '../../content/home'
import { TAGLINE } from '../../content/pages'
import { ProjectPreview } from '../previews/project-preview'

/** The Get started (primary) and Self-host (secondary) pair, in the hero and the closing CTA (FR-212). */
export function HeroButtons() {
  return (
    <div className="flex flex-wrap gap-3">
      <Link to="/register" className={buttonClasses('primary', 'px-5')}>
        Get started
      </Link>
      <Link to="/self-host" className={buttonClasses('secondary', 'px-5')}>
        Self-host
      </Link>
    </div>
  )
}

/**
 * FR-212 §1: the h1 (text, in the server HTML: the LCP element), the subhead, the two buttons and the project preview
 * over a soft brand glow (decorative, FR-202).
 */
export function HomeHero() {
  return (
    <section className="relative isolate overflow-hidden" data-testid="home-hero">
      <div aria-hidden="true" className="bg-brand-gradient pointer-events-none absolute inset-x-0 -top-40 -z-10 mx-auto h-80 max-w-3xl rounded-full opacity-15 blur-3xl" />
      <div className="mx-auto grid max-w-6xl items-center gap-12 px-4 pt-16 pb-16 sm:pt-24 lg:grid-cols-[1.05fr_1fr]">
        <div>
          <h1 className="max-w-3xl text-4xl font-semibold tracking-tight text-balance sm:text-6xl">{TAGLINE}</h1>
          <p className="mt-6 max-w-2xl text-lg text-muted-foreground">{heroSubhead}</p>
          <div className="mt-8">
            <HeroButtons />
          </div>
        </div>
        <ProjectPreview />
      </div>
    </section>
  )
}
